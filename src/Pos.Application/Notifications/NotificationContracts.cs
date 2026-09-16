using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Notifications;

namespace Pos.Application.Notifications;

/// <summary>Writes durable, deduplicated operational notifications.</summary>
public interface INotificationWriter
{
    /// <summary>Writes the notification unless its deduplication key already exists.</summary>
    Task<bool> WriteOnceAsync(Notification notification, CancellationToken cancellationToken);
}

/// <summary>One notification as exposed to authenticated clients.</summary>
public sealed record NotificationItem(
    Guid Id,
    NotificationKind Kind,
    NotificationSeverity Severity,
    string Title,
    string Body,
    Guid? LocationId,
    Guid? ProductId,
    Guid? BatchId,
    ReferenceDocumentType? ReferenceDocumentType,
    Guid? ReferenceDocumentId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc,
    DateTimeOffset? AcknowledgedAtUtc);

/// <summary>A location-scoped notification feed and its total unread count.</summary>
public sealed record NotificationFeed(IReadOnlyList<NotificationItem> Items, int UnreadCount);

/// <summary>Reads and updates one user's durable notification feed.</summary>
public interface INotificationReader
{
    /// <summary>Lists the newest notifications visible to the user.</summary>
    Task<NotificationFeed> ListAsync(
        UserId userId,
        bool unreadOnly,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Marks one visible notification read, returning false when it is outside the user's scope.</summary>
    Task<bool> MarkReadAsync(
        UserId userId,
        NotificationId notificationId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Marks every visible unread notification read.</summary>
    Task<int> MarkAllReadAsync(
        UserId userId,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Publishes a newly persisted notification to connected clients.</summary>
public interface INotificationPublisher
{
    /// <summary>Delivers the notification as a real-time hint; durable storage remains authoritative.</summary>
    Task PublishAsync(Notification notification, CancellationToken cancellationToken);
}
