using Pos.Application.Common.Abstractions;
using Pos.Domain.Auditing;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Auditing;

/// <summary>
/// Stages audit entries for commit alongside the change they describe.
/// </summary>
/// <remarks>
/// <para>
/// The entry is added to the same change tracker as the business rows and
/// committed by the same transaction. It is deliberately not written through a
/// separate connection or queued for later: an audit trail that can be missing
/// for a change that succeeded, or present for one that rolled back, is worse
/// than none, because it is trusted.
/// </para>
/// <para>
/// The actor is read from the ambient request context rather than passed in at
/// each call site, so a caller cannot record someone else as having done it.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class AuditWriter(
    PosDbContext context,
    ICurrentUser currentUser,
    ISystemClock clock) : IAuditWriter
{
    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        AuditLogEntry record = AuditLogEntry.Record(
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            clock.UtcNow,
            currentUser.CorrelationId,
            currentUser.UserId,
            currentUser.RoleSnapshot,
            currentUser.DeviceId,
            entry.LocationId,
            currentUser.IpAddress,
            currentUser.UserAgent,
            entry.PreviousValueJson,
            entry.NewValueJson,
            entry.Reason,
            entry.ReferenceDocumentType,
            entry.ReferenceDocumentId);

        context.AuditLog.Add(record);

        return Task.CompletedTask;
    }
}
