using FluentAssertions;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Notifications;

namespace Pos.Infrastructure.Tests.Notifications;

public sealed class EmergencyTransferNotificationFactoryTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 9, 17, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_AlertsBothEnds_AndReferencesTheTransfer()
    {
        TransferOrderId transferId = TransferOrderId.New();
        LocationId source = LocationId.New();
        LocationId destination = LocationId.New();
        EmergencyTransferAlert alert = new(
            transferId, "TRF-2026-000009", source, destination, _createdAt, 2, 7.5m);

        IReadOnlyList<Notification> notifications = EmergencyTransferNotificationFactory.Create(alert);

        notifications.Select(n => n.LocationId).Should().Equal(source, destination);
        notifications.Select(n => n.DeduplicationKey).Should().Equal(
            $"emergency:transfer:{transferId.Value:D}:{source.Value:D}",
            $"emergency:transfer:{transferId.Value:D}:{destination.Value:D}");

        Notification notification = notifications[0];
        notification.Kind.Should().Be(NotificationKind.EmergencyTransfer);
        notification.Severity.Should().Be(NotificationSeverity.Critical);
        notification.Title.Should().Be("Emergency transfer TRF-2026-000009 initiated");
        notification.Body.Should().Be(
            "7.5000 units across 2 lines moved under dual-manager emergency authorization. Open the transfer for central-review status.");
        notification.CreatedAtUtc.Should().Be(_createdAt);
        notification.ReferenceDocumentType.Should().Be(ReferenceDocumentType.TransferOrder);
        notification.ReferenceDocumentId.Should().Be(transferId.Value);
    }

    [Fact]
    public void Create_WithTheSameSourceAndDestination_AlertsOnce()
    {
        LocationId location = LocationId.New();
        EmergencyTransferAlert alert = new(
            TransferOrderId.New(), "TRF-2026-000010", location, location, _createdAt, 1, 3m);

        EmergencyTransferNotificationFactory.Create(alert).Should().ContainSingle()
            .Which.Body.Should().StartWith("3.0000 units across 1 line");
    }
}
