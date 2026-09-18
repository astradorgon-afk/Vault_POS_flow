using FluentAssertions;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Notifications;
using Pos.Infrastructure.Notifications;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Tests.Notifications;

/// <summary>
/// What a store is told when head office turns one of its register's events
/// away. The alert has to be readable by somebody standing near the till, and it
/// has to say which of the two failures it is: a queue that has stopped, or a
/// record that landed and needs checking.
/// </summary>
public sealed class SyncFailureNotificationFactoryTests
{
    private static readonly DateTimeOffset DecidedAt = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ForARefusedEvent_IsCriticalAndSaysTheQueueHasStopped()
    {
        EventId eventId = EventId.New();
        LocationId store = LocationId.New();
        SyncFailureAlert alert = new(
            eventId, "S1-01", store, 42, "SaleCompleted", nameof(SyncOutcome.Rejected),
            "The cashier no longer holds sales.create at this location.", DecidedAt);

        Notification notification = SyncFailureNotificationFactory.Create(alert);

        notification.Kind.Should().Be(NotificationKind.SyncFailure);
        notification.Severity.Should().Be(NotificationSeverity.Critical);
        notification.Title.Should().Be("Register S1-01: SaleCompleted refused");
        notification.LocationId.Should().Be(store);
        notification.CreatedAtUtc.Should().Be(DecidedAt);
        notification.DeduplicationKey.Should().Be($"sync:failure:{eventId.Value:D}");

        notification.Body.Should().Contain("Event 42 from register S1-01");
        notification.Body.Should().Contain("Nothing behind it in that register's queue can reach head office");

        // The server's own words travel with it. "Refused" without the reason
        // only sends somebody to the failure list to be told what they were
        // already being told.
        notification.Body.Should().Contain("The cashier no longer holds sales.create at this location.");
    }

    [Fact]
    public void Create_ForAnEventThatLandedFlagged_IsAWarning()
    {
        SyncFailureAlert alert = new(
            EventId.New(), "S1-02", LocationId.New(), 7, "SaleCompleted", nameof(SyncOutcome.RequiresReview),
            "The sale was recorded, and it took the shelf below zero.", DecidedAt);

        Notification notification = SyncFailureNotificationFactory.Create(alert);

        // The books are right and the trade is flowing. Somebody has to go and
        // look, which is not the same as somebody has to go now.
        notification.Severity.Should().Be(NotificationSeverity.Warning);
        notification.Title.Should().Be("Register S1-02: SaleCompleted needs review");
        notification.Body.Should().Contain("It was applied and flagged");
    }

    [Fact]
    public void Create_ForAConflict_IsCritical()
    {
        SyncFailureAlert alert = new(
            EventId.New(), "S1-03", LocationId.New(), 3, "TransferReceived", nameof(SyncOutcome.Conflict),
            null, DecidedAt);

        Notification notification = SyncFailureNotificationFactory.Create(alert);

        notification.Severity.Should().Be(NotificationSeverity.Critical);
        notification.Body.Should().NotContain("Server:", "there was nothing for the server to add");
    }

    [Fact]
    public void Create_WithAVeryLongServerMessage_StillFitsTheColumn()
    {
        SyncFailureAlert alert = new(
            EventId.New(), "S1-04", LocationId.New(), 9, "SaleCompleted", nameof(SyncOutcome.Rejected),
            new string('x', 4000), DecidedAt);

        Notification notification = SyncFailureNotificationFactory.Create(alert);

        // Trimmed rather than thrown, because a message the column cannot hold
        // is still a failure somebody has to be told about.
        notification.Body.Length.Should().BeLessThanOrEqualTo(Notification.BodyMaxLength);
        notification.Body.Should().EndWith("…");
    }
}
