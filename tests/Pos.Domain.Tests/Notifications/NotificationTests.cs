using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Notifications;

namespace Pos.Domain.Tests.Notifications;

public sealed class NotificationTests
{
    [Fact]
    public void Create_TrimsAndStoresTheDurableTarget()
    {
        LocationId location = LocationId.New();
        ProductId product = ProductId.New();
        BatchId batch = BatchId.New();

        Notification notification = Notification.Create(
            NotificationKind.BatchExpiringSoon,
            NotificationSeverity.Warning,
            "  Batch expiring  ",
            "  Check the shelf.  ",
            "  expiry:soon:test  ",
            DateTimeOffset.UtcNow,
            location,
            product,
            batch);

        notification.Title.Should().Be("Batch expiring");
        notification.Body.Should().Be("Check the shelf.");
        notification.DeduplicationKey.Should().Be("expiry:soon:test");
        notification.LocationId.Should().Be(location);
        notification.ProductId.Should().Be(product);
        notification.BatchId.Should().Be(batch);
    }

    [Fact]
    public void Receipt_AcknowledgeAlsoMarksTheNotificationRead()
    {
        NotificationReceipt receipt = NotificationReceipt.Create(NotificationId.New(), UserId.New());
        DateTimeOffset now = DateTimeOffset.UtcNow;

        receipt.Acknowledge(now);

        receipt.ReadAtUtc.Should().Be(now);
        receipt.AcknowledgedAtUtc.Should().Be(now);
    }
}
