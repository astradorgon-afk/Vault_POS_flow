using System.Globalization;
using Pos.Application.Notifications;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;

namespace Pos.Infrastructure.Notifications;

internal static class DiscrepancyNotificationFactory
{
    /// <summary>
    /// Builds one alert per goods receipt. Receipts are immutable once posted, so
    /// the receipt identifier alone deduplicates.
    /// </summary>
    internal static Notification CreateReceiving(ReceivingDiscrepancyAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        int count = alert.Discrepancies.Count;

        string kinds = string.Join(
            ", ",
            alert.Discrepancies
                .GroupBy(d => d.Kind)
                .OrderBy(g => g.Key)
                .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Sum(d => d.Quantity):N4}")));
        decimal value = alert.Discrepancies.Sum(d => d.ValueImpact);

        return Notification.Create(
            NotificationKind.ReceivingDiscrepancy,
            NotificationSeverity.Warning,
            $"{alert.ReceiptNumber} has {count} unresolved {(count == 1 ? "discrepancy" : "discrepancies")}",
            FormattableString.Invariant($"{kinds}; value impact {value:N2}. Resolve them on the goods receipt."),
            $"discrepancy:receipt:{alert.ReceiptId.Value:D}",
            alert.ReceivedAtUtc,
            alert.LocationId,
            referenceDocumentType: ReferenceDocumentType.GoodsReceipt,
            referenceDocumentId: alert.ReceiptId.Value);
    }

    /// <summary>
    /// Builds the alerts for a short transfer arrival: one for the destination
    /// and one for the source, since a notification is scoped to one location.
    /// Stock missing in custody is critical; the transfer cannot be verified
    /// until it is resolved.
    /// </summary>
    internal static IReadOnlyList<Notification> CreateTransferShortage(TransferShortageAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        string body = FormattableString.Invariant(
            $"{alert.ShortQuantity:N4} units missing across {alert.ShortLineCount} {(alert.ShortLineCount == 1 ? "line" : "lines")}. Resolve as found or written off before verification.");

        return new[] { alert.DestinationLocationId, alert.SourceLocationId }
            .Distinct()
            .Select(locationId => Notification.Create(
                NotificationKind.TransferShortage,
                NotificationSeverity.Critical,
                $"{alert.TransferNumber} arrived short",
                body,
                $"discrepancy:transfer:{alert.TransferId.Value:D}:{locationId.Value:D}",
                alert.ReceivedAtUtc,
                locationId,
                referenceDocumentType: ReferenceDocumentType.TransferOrder,
                referenceDocumentId: alert.TransferId.Value))
            .ToList();
    }
}
