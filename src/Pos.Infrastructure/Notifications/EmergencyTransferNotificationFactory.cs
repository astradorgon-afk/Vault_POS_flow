using Pos.Application.Notifications;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;

namespace Pos.Infrastructure.Notifications;

internal static class EmergencyTransferNotificationFactory
{
    /// <summary>Builds one critical alert for each endpoint location of an emergency transfer.</summary>
    internal static IReadOnlyList<Notification> Create(EmergencyTransferAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        string body = FormattableString.Invariant(
            $"{alert.TotalQuantity:N4} units across {alert.LineCount} {(alert.LineCount == 1 ? "line" : "lines")} moved under dual-manager emergency authorization. Open the transfer for central-review status.");

        return new[] { alert.SourceLocationId, alert.DestinationLocationId }
            .Distinct()
            .Select(locationId => Notification.Create(
                NotificationKind.EmergencyTransfer,
                NotificationSeverity.Critical,
                $"Emergency transfer {alert.TransferNumber} initiated",
                body,
                $"emergency:transfer:{alert.TransferId.Value:D}:{locationId.Value:D}",
                alert.CreatedAtUtc,
                locationId,
                referenceDocumentType: ReferenceDocumentType.TransferOrder,
                referenceDocumentId: alert.TransferId.Value))
            .ToList();
    }
}
