using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Infrastructure.Configuration;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Periodically replays the ledger and compares it with the balance projection.
/// </summary>
/// <remarks>
/// The projection is a cache of the ledger and can drift from a bug, a manual
/// database edit, or a failed migration half-way through. The worker is the
/// tripwire: it never writes, it only reports, so a malfunctioning pass cannot
/// corrupt anything. Repair is a deliberate, separately authorized act via the
/// rebuild endpoint.
/// </remarks>
/// <param name="services">Service provider, so each pass gets a fresh scope.</param>
/// <param name="options">Reconciliation schedule.</param>
/// <param name="logger">Logger.</param>
public sealed class BalanceReconcilerWorker(
    IServiceScopeFactory services,
    IOptions<ReconciliationOptions> options,
    ILogger<BalanceReconcilerWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReconciliationOptions settings = options.Value;

        if (settings.RunOnStartup)
        {
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
        }

        using PeriodicTimer timer = new(settings.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();

        IBalanceReconciler reconciler = scope.ServiceProvider.GetRequiredService<IBalanceReconciler>();

        Result<ReconciliationReport> result = await reconciler.DetectAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess && result.Value.IsHealthy && logger.IsEnabled(LogLevel.Information))
        {
            int bucketCount = result.Value.BucketCount;

            logger.LogInformation(
                "Balance reconciliation pass clean: {BucketCount} buckets agree with the ledger.",
                bucketCount);
        }
        else if (result.IsSuccess && logger.IsEnabled(LogLevel.Warning))
        {
            int driftedCount = result.Value.DriftedBucketCount;
            int discrepancyCount = result.Value.Discrepancies.Count;

            logger.LogWarning(
                "Balance reconciliation detected {DriftCount} drifted buckets: {DiscrepancyCount} discrepancies. Run the rebuild endpoint to repair.",
                driftedCount,
                discrepancyCount);
        }
        else if (logger.IsEnabled(LogLevel.Error))
        {
            string errorCode = result.Error.Code;
            string errorMessage = result.Error.Message;

            logger.LogError(
                "Balance reconciliation failed: {ErrorCode} {ErrorMessage}.",
                errorCode,
                errorMessage);
        }
    }
}