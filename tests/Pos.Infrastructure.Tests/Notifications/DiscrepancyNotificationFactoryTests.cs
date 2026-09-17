using System.Globalization;
using FluentAssertions;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;
using Pos.Domain.Purchasing;
using Pos.Infrastructure.Notifications;

namespace Pos.Infrastructure.Tests.Notifications;

public sealed class DiscrepancyNotificationFactoryTests
{
    private static readonly DateTimeOffset _receivedAt = new(2026, 9, 17, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateReceiving_SummarisesKindsAndValue_AndReferencesTheReceipt()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            GoodsReceiptId receiptId = GoodsReceiptId.New();
            LocationId locationId = LocationId.New();
            ReceivingDiscrepancyAlert alert = new(
                receiptId,
                "GRN-2026-000042",
                locationId,
                _receivedAt,
                [
                    new(ReceivingDiscrepancyKind.Damaged, 1m, 100m),
                    new(ReceivingDiscrepancyKind.Shortage, 4m, 400m),
                    new(ReceivingDiscrepancyKind.Shortage, 2m, 1250.5m),
                ]);

            Notification notification = DiscrepancyNotificationFactory.CreateReceiving(alert);

            notification.Kind.Should().Be(NotificationKind.ReceivingDiscrepancy);
            notification.Severity.Should().Be(NotificationSeverity.Warning);
            notification.Title.Should().Be("GRN-2026-000042 has 3 unresolved discrepancies");
            notification.Body.Should().Be(
                "Shortage 6.0000, Damaged 1.0000; value impact 1,750.50. Resolve them on the goods receipt.");
            notification.DeduplicationKey.Should().Be($"discrepancy:receipt:{receiptId.Value:D}");
            notification.CreatedAtUtc.Should().Be(_receivedAt);
            notification.LocationId.Should().Be(locationId);
            notification.ReferenceDocumentType.Should().Be(ReferenceDocumentType.GoodsReceipt);
            notification.ReferenceDocumentId.Should().Be(receiptId.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void CreateReceiving_WithOneDiscrepancy_UsesTheSingularLabel()
    {
        ReceivingDiscrepancyAlert alert = new(
            GoodsReceiptId.New(), "GRN-2026-000043", LocationId.New(), _receivedAt,
            [new(ReceivingDiscrepancyKind.MissingDocuments, 0m, 0m)]);

        DiscrepancyNotificationFactory.CreateReceiving(alert).Title
            .Should().Be("GRN-2026-000043 has 1 unresolved discrepancy");
    }

    [Fact]
    public void CreateTransferShortage_AlertsBothEnds_AndReferencesTheTransfer()
    {
        TransferOrderId transferId = TransferOrderId.New();
        LocationId source = LocationId.New();
        LocationId destination = LocationId.New();
        TransferShortageAlert alert = new(transferId, "TRF-2026-000007", source, destination, _receivedAt, 1, 2m);

        IReadOnlyList<Notification> notifications = DiscrepancyNotificationFactory.CreateTransferShortage(alert);

        notifications.Select(n => n.LocationId).Should().Equal(destination, source);
        notifications.Select(n => n.DeduplicationKey).Should().Equal(
            $"discrepancy:transfer:{transferId.Value:D}:{destination.Value:D}",
            $"discrepancy:transfer:{transferId.Value:D}:{source.Value:D}");

        Notification notification = notifications[0];
        notification.Kind.Should().Be(NotificationKind.TransferShortage);
        notification.Severity.Should().Be(NotificationSeverity.Critical);
        notification.Title.Should().Be("TRF-2026-000007 arrived short");
        notification.Body.Should().Be(
            "2.0000 units missing across 1 line. Resolve as found or written off before verification.");
        notification.ReferenceDocumentType.Should().Be(ReferenceDocumentType.TransferOrder);
        notification.ReferenceDocumentId.Should().Be(transferId.Value);
    }

    [Fact]
    public void CreateTransferShortage_WithTheSameSourceAndDestination_AlertsOnce()
    {
        LocationId location = LocationId.New();
        TransferShortageAlert alert = new(TransferOrderId.New(), "TRF-2026-000008", location, location, _receivedAt, 2, 3m);

        DiscrepancyNotificationFactory.CreateTransferShortage(alert).Should().ContainSingle()
            .Which.Body.Should().StartWith("3.0000 units missing across 2 lines.");
    }
}
