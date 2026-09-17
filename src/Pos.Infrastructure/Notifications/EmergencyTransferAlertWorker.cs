using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Notifications;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Configuration;

namespace Pos.Infrastructure.Notifications;

/// <summary>
/// Periodically announces recently committed emergency transfers to their source
/// and destination locations. Durable deduplication makes the lookback safe
/// across restarts and lets all-location users receive the same events.
/// </summary>
/// <param name="services">Service provider, so each pass gets a fresh scope.</param>
/// <param name="options">Worker schedule and lookback.</param>
/// <param name="clock">System clock.</param>
/// <param name="logger">Logger.</param>
public sealed class EmergencyTransferAlertWorker(
    IServiceScopeFactory services,
    IOptions<EmergencyTransferAlertOptions> options,
    ISystemClock clock,
    ILogger<EmergencyTransferAlertWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EmergencyTransferAlertOptions settings = options.Value;

        if (settings.RunOnStartup)
        {
            await SweepSafelyAsync(stoppingToken).ConfigureAwait(false);
        }

        using PeriodicTimer timer = new(settings.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one pass and returns how many new alerts were written.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of new notifications.</returns>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();

        IEmergencyTransferAlertRepository repository =
            scope.ServiceProvider.GetRequiredService<IEmergencyTransferAlertRepository>();
        INotificationWriter notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        DateTimeOffset since = clock.UtcNow - options.Value.Lookback;
        IReadOnlyList<EmergencyTransferAlert> transfers = await repository
            .GetRecentAsync(since, cancellationToken)
            .ConfigureAwait(false);

        int written = 0;
        foreach (Notification alert in transfers.SelectMany(EmergencyTransferNotificationFactory.Create))
        {
            if (await notifications.WriteOnceAsync(alert, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Emergency-transfer sweep completed: {TransferCount} transfers found, {NewAlerts} new alerts.",
                transfers.Count,
                written);
        }

        return written;
    }

    private async Task SweepSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One failed pass must not stop the hosted service; the next tick retries.
            logger.LogError(ex, "Emergency-transfer alert sweep failed.");
        }
    }
}
