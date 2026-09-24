using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// The unit of work a device command runs inside. It is the same contract the
/// server implements over PostgreSQL, against the device's SQLite file: an
/// offline shift either fully happened or did not happen.
/// </summary>
/// <param name="context">The scoped device context.</param>
public sealed class DeviceUnitOfWork(PosDeviceDbContext context) : IUnitOfWork
{
    /// <inheritdoc />
    public bool HasActiveTransaction => context.Database.CurrentTransaction is not null;

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        => context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public bool IsConcurrencyConflict(Exception exception)
        => exception is ConcurrencyConflictException or Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException;

    /// <inheritdoc />
    public void DiscardChanges() => context.ChangeTracker.Clear();

    /// <inheritdoc />
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        => new DeviceTransaction(
            await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));

    private sealed class DeviceTransaction(IDbContextTransaction transaction) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
            => transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken)
            => transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}

/// <summary>
/// Who is signed in at this register. A device has one session at a time — one
/// drawer, one cashier — so this is a singleton the sign-in screen sets and
/// sign-out clears.
/// </summary>
/// <remarks>
/// It holds no credential and grants nothing. Authority still comes from the
/// cached permission snapshot, which expires;
/// <see cref="DeviceSnapshotPermissionEvaluator"/> is what answers whether this
/// user may do anything, and it refuses a user whose snapshot has run out even
/// while that user is still signed in here.
/// </remarks>
public sealed class DeviceSession
{
    private readonly Lock gate = new();
    private SignedIn? signedIn;

    /// <summary>Gets the signed-in user, or null when the register is locked.</summary>
    public UserId? UserId
    {
        get
        {
            lock (this.gate)
            {
                return this.signedIn?.UserId;
            }
        }
    }

    /// <summary>Gets this device's identity, or null before enrolment.</summary>
    public DeviceId? DeviceId
    {
        get
        {
            lock (this.gate)
            {
                return this.signedIn?.DeviceId;
            }
        }
    }

    /// <summary>Gets the location this register trades at, or null before enrolment.</summary>
    public LocationId? LocationId
    {
        get
        {
            lock (this.gate)
            {
                return this.signedIn?.LocationId;
            }
        }
    }

    /// <summary>Gets the correlation identifier of the current session.</summary>
    public CorrelationId CorrelationId
    {
        get
        {
            lock (this.gate)
            {
                return this.signedIn?.CorrelationId ?? CorrelationId.New();
            }
        }
    }

    /// <summary>Records that a cashier has signed in at this register.</summary>
    /// <param name="userId">The cashier.</param>
    /// <param name="deviceId">This device.</param>
    /// <param name="locationId">The location this device trades at.</param>
    public void SignIn(UserId userId, DeviceId deviceId, LocationId locationId)
    {
        lock (this.gate)
        {
            this.signedIn = new SignedIn(userId, deviceId, locationId, CorrelationId.New());
        }
    }

    /// <summary>Clears the session. Anything still in the outbox stays there.</summary>
    public void SignOut()
    {
        lock (this.gate)
        {
            this.signedIn = null;
        }
    }

    private sealed record SignedIn(
        UserId UserId,
        DeviceId DeviceId,
        LocationId LocationId,
        CorrelationId CorrelationId);
}

/// <summary>
/// The caller, as a device knows one. There is no request and no IP address
/// here: the caller is whoever is standing at the register.
/// </summary>
/// <param name="session">The register's session.</param>
public sealed class DeviceCurrentUser(DeviceSession session) : ICurrentUser
{
    /// <inheritdoc />
    public UserId? UserId => session.UserId;

    /// <inheritdoc />
    public DeviceId? DeviceId => session.DeviceId;

    /// <inheritdoc />
    public IReadOnlyCollection<LocationId> AssignedLocations
        => session.LocationId is { } location ? [location] : [];

    /// <inheritdoc />
    /// <remarks>
    /// Always false. A device acts at its own location and nowhere else, so the
    /// business-wide escape hatch does not exist offline — which is the same
    /// rule as the permission catalogue's, where <c>location.all</c> is not
    /// offline-capable.
    /// </remarks>
    public bool HasAllLocations => false;

    /// <inheritdoc />
    public CorrelationId CorrelationId => session.CorrelationId;

    /// <inheritdoc />
    /// <remarks>A device has no remote caller; the caller is at the counter.</remarks>
    public string? IpAddress => null;

    /// <inheritdoc />
    public string? UserAgent => null;

    /// <inheritdoc />
    /// <remarks>
    /// Null offline. Roles are a server concept the device does not cache — it
    /// caches the permissions those roles resolved to — and inventing one here
    /// would put a guess in the audit log, where the whole value is that it
    /// records what was actually known.
    /// </remarks>
    public string? RoleSnapshot => null;
}

/// <summary>
/// Writes audit entries into the device's own append-only table, inside the
/// same transaction as the change they describe.
/// </summary>
/// <remarks>
/// The device is the only witness to what happened on it while it was offline,
/// so an entry is written locally and travels with the event it describes. It is
/// never rewritten and never deleted.
/// </remarks>
/// <param name="context">The scoped device context.</param>
/// <param name="currentUser">The caller, stamped onto the entry.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceAuditWriter(
    PosDeviceDbContext context,
    ICurrentUser currentUser,
    ISystemClock clock) : IAuditWriter
{
    /// <inheritdoc />
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        context.LocalAudit.Add(new DeviceLocalAudit(
            Guid.CreateVersion7(),
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            currentUser.UserId,
            currentUser.DeviceId,
            entry.LocationId,
            entry.PreviousValueJson,
            entry.NewValueJson,
            entry.Reason,
            clock.UtcNow));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Collects draws the ledger refused for driving stock negative.
/// </summary>
/// <remarks>
/// Nothing writes them yet: a device has no ledger, because it has no local
/// movement tables, so nothing on a device can refuse a draw. The recorder
/// exists because the pipeline behaviour resolves it on every command, and a
/// device that could not compose its own pipeline would refuse every use case
/// for the wrong reason. When the device ledger lands, this is where its refused
/// draws go.
/// </remarks>
public sealed class DeviceNegativeStockAttemptRecorder : INegativeStockAttemptRecorder
{
    private readonly List<NegativeStockAttempt> pending = [];

    /// <inheritdoc />
    public bool HasPending => this.pending.Count > 0;

    /// <inheritdoc />
    public void Record(NegativeStockAttempt attempt) => this.pending.Add(attempt);

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        this.pending.Clear();
        return Task.CompletedTask;
    }
}
