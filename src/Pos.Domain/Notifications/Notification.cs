using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Notifications;

/// <summary>Stable notification kinds used by storage and clients.</summary>
public enum NotificationKind : short
{
    BatchExpiringSoon = 1,
    BatchExpired = 2,
}

/// <summary>The attention level of a notification.</summary>
public enum NotificationSeverity : short
{
    Info = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>A durable operational notification. SignalR may accelerate delivery, but this row is the source of truth.</summary>
public sealed class Notification : Entity<NotificationId>
{
    /// <summary>Maximum title length.</summary>
    public const int TitleMaxLength = 160;

    /// <summary>Maximum body length.</summary>
    public const int BodyMaxLength = 1000;

    /// <summary>Maximum deduplication-key length.</summary>
    public const int DeduplicationKeyMaxLength = 240;

    private Notification() { Title = string.Empty; Body = string.Empty; DeduplicationKey = string.Empty; }

    private Notification(NotificationKind kind, NotificationSeverity severity, string title, string body,
        string deduplicationKey, LocationId? locationId, ProductId? productId, BatchId? batchId,
        ReferenceDocumentType? referenceDocumentType, Guid? referenceDocumentId, DateTimeOffset createdAtUtc)
    {
        Id = NotificationId.New(); Kind = kind; Severity = severity; Title = title; Body = body;
        DeduplicationKey = deduplicationKey; LocationId = locationId; ProductId = productId; BatchId = batchId;
        ReferenceDocumentType = referenceDocumentType; ReferenceDocumentId = referenceDocumentId; CreatedAtUtc = createdAtUtc;
    }

    public NotificationKind Kind { get; private init; }
    public NotificationSeverity Severity { get; private init; }
    public string Title { get; private init; }
    public string Body { get; private init; }
    public string DeduplicationKey { get; private init; }
    public LocationId? LocationId { get; private init; }
    public ProductId? ProductId { get; private init; }
    public BatchId? BatchId { get; private init; }
    public ReferenceDocumentType? ReferenceDocumentType { get; private init; }
    public Guid? ReferenceDocumentId { get; private init; }
    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>Creates a validated durable notification.</summary>
    public static Notification Create(NotificationKind kind, NotificationSeverity severity, string title, string body,
        string deduplicationKey, DateTimeOffset createdAtUtc, LocationId? locationId = null,
        ProductId? productId = null, BatchId? batchId = null,
        ReferenceDocumentType? referenceDocumentType = null, Guid? referenceDocumentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        if (title.Trim().Length > TitleMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(title));
        }

        if (body.Trim().Length > BodyMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(body));
        }

        if (deduplicationKey.Trim().Length > DeduplicationKeyMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(deduplicationKey));
        }
        return new Notification(kind, severity, title.Trim(), body.Trim(), deduplicationKey.Trim(),
            locationId, productId, batchId, referenceDocumentType, referenceDocumentId, createdAtUtc);
    }
}

/// <summary>Per-user read and acknowledgement state for a notification.</summary>
public sealed class NotificationReceipt
{
    private NotificationReceipt() { }
    private NotificationReceipt(NotificationId notificationId, UserId userId) { NotificationId = notificationId; UserId = userId; }
    public NotificationId NotificationId { get; private init; }
    public UserId UserId { get; private init; }
    public DateTimeOffset? ReadAtUtc { get; private set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }
    public static NotificationReceipt Create(NotificationId notificationId, UserId userId) => new(notificationId, userId);
    public void MarkRead(DateTimeOffset atUtc) => ReadAtUtc ??= atUtc;
    public void Acknowledge(DateTimeOffset atUtc) { ReadAtUtc ??= atUtc; AcknowledgedAtUtc ??= atUtc; }
}
