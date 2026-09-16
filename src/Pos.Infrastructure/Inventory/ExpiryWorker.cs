using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Configuration;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Periodically scans all stocking locations for batches past their expiry
/// date and posts ExpiryQuarantine movements to move them into the Expired
/// state. Runs as a system-level background service using a system actor with
/// no authenticated user context.
/// </summary>
/// <remarks>
/// Each location's run is independent: a failure at one location does not
/// prevent the sweep from continuing to the next. The worker uses a fresh
/// scope per pass to avoid holding a long-lived DbContext.
/// </remarks>
/// <param name="services">Service provider, so each pass gets a fresh scope.</param>
/// <param name="options">Worker schedule.</param>
/// <param name="clock">System clock.</param>
/// <param name="logger">Logger.</param>
public sealed class ExpiryWorker(
    IServiceScopeFactory services,
    IOptions<ExpiryOptions> options,
    ISystemClock clock,
    ILogger<ExpiryWorker> logger) : BackgroundService
{
    /// <summary>The system actor used when no authenticated user is present.</summary>
    private static readonly LedgerActor SystemActor = new(
        CreatedBy: UserId.Empty,
        ApprovedBy: null,
        Device: null,
        Correlation: CorrelationId.New());

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ExpiryOptions settings = options.Value;

        if (settings.RunOnStartup)
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }

        using PeriodicTimer timer = new(settings.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();

        IExpiryService expiryService = scope.ServiceProvider.GetRequiredService<IExpiryService>();
        IExpiryRepository repository = scope.ServiceProvider.GetRequiredService<IExpiryRepository>();
        INotificationWriter notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        IReadOnlyList<ExpiryLocationInfo> locations = await repository
            .GetAllStockingLocationsAsync(cancellationToken)
            .ConfigureAwait(false);

        int expiredCount = 0;
        int errorCount = 0;

        foreach (ExpiryLocationInfo location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.TimeZoneId))
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        "Skipping expiry sweep for location {LocationId}: no time zone configured.",
                        location.LocationId.Value);
                }

                continue;
            }

            IReadOnlyList<ExpiringBatchSummary> expiring = await expiryService
                .GetExpiringBatchesAsync(location.LocationId, cancellationToken)
                .ConfigureAwait(false);

            foreach (ExpiringBatchSummary item in expiring)
            {
                Notification alert = ExpiryNotificationFactory.CreateExpiringBatch(item, clock.UtcNow);

                await notifications.WriteOnceAsync(alert, cancellationToken).ConfigureAwait(false);
            }

            Result<ExpiryRunResult> result = await expiryService
                .PostExpiryRunAsync(location.LocationId, SystemActor, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                expiredCount += result.Value.ExpiredBatchesCount;

                Notification alert = ExpiryNotificationFactory.CreateExpiredRun(result.Value);

                await notifications.WriteOnceAsync(alert, cancellationToken).ConfigureAwait(false);

                if (result.Value.ExpiredBatchesCount > 0 && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Expiry quarantine sweep completed at location {LocationId}: {BatchCount} batches, {TotalValue:N2} total.",
                        location.LocationId.Value,
                        result.Value.ExpiredBatchesCount,
                        result.Value.TotalValue);
                }
            }
            else if (result.Error.Code != ExpiryErrors.NoExpiredBatches.Code)
            {
                errorCount++;

                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        "Expiry sweep failed at location {LocationId}: {ErrorCode} {ErrorMessage}.",
                        location.LocationId.Value,
                        result.Error.Code,
                        result.Error.Message);
                }
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Expiry sweep pass completed: {ExpiredCount} expired batches across {LocationCount} locations ({ErrorCount} errors).",
                expiredCount,
                locations.Count,
                errorCount);
        }
    }
}
