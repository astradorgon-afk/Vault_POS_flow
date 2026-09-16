using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Application.Notifications;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Configuration;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Periodically raises location-scoped alerts for stocked products at or below
/// their reorder point. Alerts are deduplicated per product, level and UTC day.
/// </summary>
/// <param name="services">Service provider, so each pass gets a fresh scope.</param>
/// <param name="options">Worker schedule.</param>
/// <param name="clock">System clock.</param>
/// <param name="logger">Logger.</param>
public sealed class LowStockWorker(
    IServiceScopeFactory services,
    IOptions<LowStockOptions> options,
    ISystemClock clock,
    ILogger<LowStockWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LowStockOptions settings = options.Value;

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

        ILowStockRepository repository = scope.ServiceProvider.GetRequiredService<ILowStockRepository>();
        INotificationWriter notifications = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        IReadOnlyList<LowStockItem> items = await repository.GetLowStockAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock.UtcNow;
        int written = 0;

        foreach (LowStockItem item in items)
        {
            Notification alert = LowStockNotificationFactory.Create(item, now);

            if (await notifications.WriteOnceAsync(alert, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Low-stock sweep completed: {LowCount} low products, {NewAlerts} new alerts.",
                items.Count,
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
            logger.LogError(ex, "Low-stock sweep failed.");
        }
    }
}
