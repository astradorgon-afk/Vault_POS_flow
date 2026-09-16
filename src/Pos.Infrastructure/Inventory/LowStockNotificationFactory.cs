using Pos.Application.Inventory;
using Pos.Domain.Notifications;

namespace Pos.Infrastructure.Inventory;

internal static class LowStockNotificationFactory
{
    /// <summary>
    /// Builds a low-stock alert. The deduplication key carries the level and the
    /// UTC day, so a product that stays low is reminded once a day and a drop to a
    /// worse level alerts straight away.
    /// </summary>
    internal static Notification Create(LowStockItem item, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(item);

        (string level, NotificationSeverity severity, string title) = item switch
        {
            { Available: <= 0m } => ("out", NotificationSeverity.Critical, $"{item.ProductName} is out of stock"),
            _ when item.Available < item.MinimumStock =>
                ("minimum", NotificationSeverity.Critical, $"{item.ProductName} is below minimum stock"),
            _ => ("reorder", NotificationSeverity.Warning, $"{item.ProductName} reached its reorder point"),
        };

        return Notification.Create(
            NotificationKind.LowStock,
            severity,
            title,
            FormattableString.Invariant(
                $"{item.Available:N4} units available; minimum {item.MinimumStock:N4}, reorder point {item.ReorderPoint:N4}."),
            $"lowstock:{item.LocationId.Value:D}:{item.ProductId.Value:D}:{level}:{createdAtUtc.UtcDateTime:yyyyMMdd}",
            createdAtUtc,
            item.LocationId,
            item.ProductId);
    }
}
