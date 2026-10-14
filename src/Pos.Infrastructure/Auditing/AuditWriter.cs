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
/// <para>
/// Sign-in, sign-out and token events are the exception, and the reason the
/// entry may name an actor: they are decided before there is an authenticated
/// caller, so the ambient identity is empty and every one of them used to read
/// back as "System". The ambient identity still wins wherever there is one, so
/// an authenticated caller cannot attribute its own action to anybody else.
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

        bool anonymous = currentUser.UserId is null;

        AuditLogEntry record = AuditLogEntry.Record(
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            clock.UtcNow,
            currentUser.CorrelationId,
            anonymous ? entry.ActorUserId : currentUser.UserId,
            anonymous ? entry.ActorRoleSnapshot : currentUser.RoleSnapshot,
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
