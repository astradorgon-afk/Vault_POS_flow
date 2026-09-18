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
/// Periodically announces the uploaded events head office turned away or set
/// aside, to the store whose register produced them.
/// </summary>
/// <remarks>
/// <para>
/// The failure list (<c>GET /api/v1/sync/failures</c>) has existed since C53 and
/// nothing told anybody to open it. A refused event sits at the head of a
/// register's queue and everything behind it waits, so the failure this exists
/// to prevent is not the refusal — it is nobody noticing the refusal.
/// </para>
/// <para>
/// Deduplication is by event identifier, so one failure raises one alert however
/// many sweeps see it. That matters more here than for the other generators: a
/// register retries a refused event for as long as it stands, and an alert per
/// retry would bury the one that mattered.
/// </para>
/// </remarks>
/// <param name="services">Service provider, so each pass gets a fresh scope.</param>
/// <param name="options">Worker schedule and lookback.</param>
/// <param name="clock">System clock.</param>
/// <param name="logger">Logger.</param>
public sealed class SyncFailureAlertWorker(
    IServiceScopeFactory services,
    IOptions<SyncFailureAlertOptions> options,
    ISystemClock clock,
    ILogger<SyncFailureAlertWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SyncFailureAlertOptions settings = options.Value;

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

        ISyncFailureAlertRepository repository =
            scope.ServiceProvider.GetRequiredService<ISyncFailureAlertRepository>();
        INotificationWriter notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        DateTimeOffset since = clock.UtcNow - options.Value.Lookback;
        IReadOnlyList<SyncFailureAlert> failures = await repository
            .GetRecentAsync(since, cancellationToken)
            .ConfigureAwait(false);

        int written = 0;
        foreach (SyncFailureAlert failure in failures)
        {
            Notification alert = SyncFailureNotificationFactory.Create(failure);

            if (await notifications.WriteOnceAsync(alert, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Sync-failure sweep completed: {FailureCount} failures found, {NewAlerts} new alerts.",
                failures.Count,
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
            logger.LogError(ex, "Sync-failure alert sweep failed.");
        }
    }
}
