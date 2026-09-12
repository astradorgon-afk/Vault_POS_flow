using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Auditing;

/// <summary>
/// One recorded action, written inside the same transaction as the change it
/// describes.
/// </summary>
/// <remarks>
/// <para>
/// Append-only and immutable, under the same four guards as the inventory
/// ledger: no setters here, an EF interceptor that rejects modification, a
/// database trigger, and an application database role with only SELECT and
/// INSERT on the table. An audit log that can be edited is not an audit log.
/// </para>
/// <para>
/// The role is snapshotted rather than joined, because the question a reviewer
/// asks is "what authority did this person have at the time", not "what
/// authority do they have now".
/// </para>
/// </remarks>
public sealed class AuditLogEntry : Entity<AuditLogId>
{
    internal AuditLogEntry(
        AuditLogId id,
        string action,
        string entityType,
        Guid? entityId,
        UserId? userId,
        string? userRoleSnapshot,
        DeviceId? deviceId,
        LocationId? locationId,
        string? ipAddress,
        string? userAgent,
        string? previousValueJson,
        string? newValueJson,
        string? reason,
        ReferenceDocumentType? referenceDocumentType,
        Guid? referenceDocumentId,
        DateTimeOffset occurredAtUtc,
        CorrelationId correlationId)
    {
        Id = id;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        UserId = userId;
        UserRoleSnapshot = userRoleSnapshot;
        DeviceId = deviceId;
        LocationId = locationId;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        PreviousValueJson = previousValueJson;
        NewValueJson = newValueJson;
        Reason = reason;
        ReferenceDocumentType = referenceDocumentType;
        ReferenceDocumentId = referenceDocumentId;
        OccurredAtUtc = occurredAtUtc;
        CorrelationId = correlationId;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private AuditLogEntry()
    {
        Action = string.Empty;
        EntityType = string.Empty;
    }

    /// <summary>Gets the stable action code, for example <c>transfer.dispatched</c>.</summary>
    public string Action { get; private init; }

    /// <summary>Gets the type of record affected.</summary>
    public string EntityType { get; private init; }

    /// <summary>Gets the identifier of the record affected.</summary>
    public Guid? EntityId { get; private init; }

    /// <summary>Gets the acting user, or null for an unauthenticated attempt.</summary>
    public UserId? UserId { get; private init; }

    /// <summary>Gets the roles the user held at the moment of the action.</summary>
    public string? UserRoleSnapshot { get; private init; }

    /// <summary>Gets the device the action came from, if any.</summary>
    public DeviceId? DeviceId { get; private init; }

    /// <summary>Gets the location the action applies to, if any.</summary>
    public LocationId? LocationId { get; private init; }

    /// <summary>Gets the caller's network address, where known.</summary>
    public string? IpAddress { get; private init; }

    /// <summary>Gets the caller's user agent, where known.</summary>
    public string? UserAgent { get; private init; }

    /// <summary>Gets the prior state, scrubbed of sensitive fields.</summary>
    public string? PreviousValueJson { get; private init; }

    /// <summary>Gets the new state, scrubbed of sensitive fields.</summary>
    public string? NewValueJson { get; private init; }

    /// <summary>Gets the reason the actor supplied, where one was required.</summary>
    public string? Reason { get; private init; }

    /// <summary>Gets the kind of document this action relates to.</summary>
    public ReferenceDocumentType? ReferenceDocumentType { get; private init; }

    /// <summary>Gets the identifier of the related document.</summary>
    public Guid? ReferenceDocumentId { get; private init; }

    /// <summary>Gets the server time the action was recorded.</summary>
    public DateTimeOffset OccurredAtUtc { get; private init; }

    /// <summary>Gets the identifier correlating this entry with the wider operation.</summary>
    public CorrelationId CorrelationId { get; private init; }

    /// <summary>Creates an entry. Called only by the audit writer.</summary>
    /// <param name="action">Stable action code.</param>
    /// <param name="entityType">Type of record affected.</param>
    /// <param name="entityId">Identifier of the record affected.</param>
    /// <param name="occurredAtUtc">Server time.</param>
    /// <param name="correlationId">Correlation identifier.</param>
    /// <param name="userId">Acting user.</param>
    /// <param name="userRoleSnapshot">Roles held at the time.</param>
    /// <param name="deviceId">Originating device.</param>
    /// <param name="locationId">Location scope.</param>
    /// <param name="ipAddress">Caller address.</param>
    /// <param name="userAgent">Caller user agent.</param>
    /// <param name="previousValueJson">Prior state.</param>
    /// <param name="newValueJson">New state.</param>
    /// <param name="reason">Reason supplied.</param>
    /// <param name="referenceDocumentType">Related document type.</param>
    /// <param name="referenceDocumentId">Related document identifier.</param>
    /// <returns>The entry.</returns>
    public static AuditLogEntry Record(
        string action,
        string entityType,
        Guid? entityId,
        DateTimeOffset occurredAtUtc,
        CorrelationId correlationId,
        UserId? userId = null,
        string? userRoleSnapshot = null,
        DeviceId? deviceId = null,
        LocationId? locationId = null,
        string? ipAddress = null,
        string? userAgent = null,
        string? previousValueJson = null,
        string? newValueJson = null,
        string? reason = null,
        ReferenceDocumentType? referenceDocumentType = null,
        Guid? referenceDocumentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        return new AuditLogEntry(
            AuditLogId.New(),
            action,
            entityType,
            entityId,
            userId,
            userRoleSnapshot,
            deviceId,
            locationId,
            ipAddress,
            userAgent,
            previousValueJson,
            newValueJson,
            reason,
            referenceDocumentType,
            referenceDocumentId,
            occurredAtUtc,
            correlationId);
    }
}
