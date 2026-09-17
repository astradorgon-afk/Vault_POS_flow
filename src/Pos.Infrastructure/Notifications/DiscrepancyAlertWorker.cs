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
/// Periodically raises alerts for recent goods receipts and transfer arrivals
/// that still carry unresolved discrepancies: one for the receiving location of
/// a receipt, and one each for the source and destination of a short transfer.
/// A document resolved before the next pass never alerts.
/// </summary>
/// <param name="services">Service provider, so each pass gets a fresh scope.</param>
/// <param name="options">Worker schedule and lookback.</param>
/// <param name="clock">System clock.</param>
/// <param name="logger">Logger.</param>
public sealed class DiscrepancyAlertWorker(
    IServiceScopeFactory services,
    IOptions<DiscrepancyAlertOptions> options,
    ISystemClock clock,
    ILogger<DiscrepancyAlertWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DiscrepancyAlertOptions settings = options.Value;

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

        IDiscrepancyAlertRepository repository = scope.ServiceProvider.GetRequiredService<IDiscrepancyAlertRepository>();
        INotificationWriter notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        DateTimeOffset since = clock.UtcNow - options.Value.Lookback;

        IReadOnlyList<ReceivingDiscrepancyAlert> receipts = await repository
            .GetOpenReceivingDiscrepanciesAsync(since, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<TransferShortageAlert> transfers = await repository
            .GetOpenTransferShortagesAsync(since, cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<Notification> alerts = receipts
            .Select(DiscrepancyNotificationFactory.CreateReceiving)
            .Concat(transfers.SelectMany(DiscrepancyNotificationFactory.CreateTransferShortage));

        int written = 0;
        foreach (Notification alert in alerts)
        {
            if (await notifications.WriteOnceAsync(alert, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Discrepancy sweep completed: {ReceiptCount} receipts and {TransferCount} transfers open, {NewAlerts} new alerts.",
                receipts.Count,
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
            logger.LogError(ex, "Discrepancy sweep failed.");
        }
    }
}
