using Pos.Domain.Inventory;
using Pos.Domain.Notifications;

namespace Pos.Infrastructure.Inventory;

internal static class ExpiryNotificationFactory
{
    internal static Notification CreateExpiringBatch(
        ExpiringBatchSummary item,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        NotificationSeverity severity = item.DaysUntilExpiry <= 7
            ? NotificationSeverity.Critical
            : NotificationSeverity.Warning;

        return Notification.Create(
            NotificationKind.BatchExpiringSoon,
            severity,
            $"{item.ProductName} expires in {item.DaysUntilExpiry} {DayLabel(item.DaysUntilExpiry)}",
            FormattableString.Invariant(
                $"Lot {item.LotNumber} has {item.Quantity:N4} units available and expires on {item.ExpiresOn:yyyy-MM-dd}."),
            $"expiry:soon:{item.LocationId.Value:D}:{item.BatchId.Value:D}:{item.ExpiresOn:yyyyMMdd}:{severity.ToString().ToLowerInvariant()}",
            createdAtUtc,
            item.LocationId,
            item.ProductId,
            item.BatchId);
    }

    internal static Notification CreateExpiredRun(ExpiryRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return Notification.Create(
            NotificationKind.BatchExpired,
            NotificationSeverity.Critical,
            $"{run.ExpiredBatchesCount} expired {BatchLabel(run.ExpiredBatchesCount)} quarantined",
            FormattableString.Invariant(
                $"Expiry run {run.RunNumber.Value} moved {run.TotalQuantity:N4} units valued at {run.TotalValue:N2} out of available stock."),
            $"expiry:expired:{run.LocationId.Value:D}:{run.RunNumber.Value}",
            run.ProcessedAtUtc,
            run.LocationId);
    }

    private static string DayLabel(int count) => count == 1 ? "day" : "days";

    private static string BatchLabel(int count) => count == 1 ? "batch" : "batches";
}
