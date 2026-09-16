using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Application.Common.Abstractions;

/// <summary>
/// Supplies the current time. Injected rather than called statically so that
/// time-dependent rules (shift windows, token expiry, expiry warnings) are
/// testable, and so the server clock is unmistakably the authoritative one.
/// </summary>
public interface ISystemClock
{
    /// <summary>Gets the current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Gets the trading day for a location, applying that location's timezone to
    /// the current instant.
    /// </summary>
    /// <param name="timeZoneId">The IANA timezone identifier of the location.</param>
    /// <returns>The local business date.</returns>
    DateOnly BusinessDateFor(string timeZoneId);
}

/// <summary>Identifies who is making the current request.</summary>
public interface ICurrentUser
{
    /// <summary>Gets the authenticated user, or <see langword="null"/> when anonymous.</summary>
    UserId? UserId { get; }

    /// <summary>Gets the device the request came from, when it is a device request.</summary>
    DeviceId? DeviceId { get; }

    /// <summary>Gets the locations the user is assigned to.</summary>
    IReadOnlyCollection<LocationId> AssignedLocations { get; }

    /// <summary>Gets a value indicating whether the user may act across all locations.</summary>
    bool HasAllLocations { get; }

    /// <summary>Gets the correlation identifier for the current operation.</summary>
    CorrelationId CorrelationId { get; }

    /// <summary>Gets the caller's IP address, where known.</summary>
    string? IpAddress { get; }

    /// <summary>Gets the caller's user agent, where known.</summary>
    string? UserAgent { get; }

    /// <summary>
    /// Gets the roles the caller held when the request began, comma separated.
    /// </summary>
    /// <remarks>
    /// Snapshotted onto each audit entry rather than joined at read time. The
    /// question an investigation asks is what authority someone had when they
    /// acted, not what they have now.
    /// </remarks>
    string? RoleSnapshot { get; }
}

/// <summary>
/// Answers permission questions. The implementation resolves roles, overrides
/// and, on a device, the cached snapshot; callers never reason about roles.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>Determines whether a user holds a permission at a location.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="permissionCode">The permission code.</param>
    /// <param name="locationId">The location scope, or <see langword="null"/> for an unscoped check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the user is authorized.</returns>
    Task<bool> HasPermissionAsync(
        UserId userId,
        string permissionCode,
        LocationId? locationId,
        CancellationToken cancellationToken);

    /// <summary>Gets every permission a user currently holds.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective permission codes.</returns>
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(UserId userId, CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether an action of a given value may proceed, and who must approve
/// it. On a device this is what turns an approval into a pending-central-review
/// state instead of silently allowing it.
/// </summary>
public interface IApprovalGate
{
    /// <summary>Checks an approval requirement.</summary>
    /// <param name="action">The action being approved, for example <c>inventory.adjust</c>.</param>
    /// <param name="absoluteValue">The absolute monetary value at stake.</param>
    /// <param name="approver">The proposed approver.</param>
    /// <param name="documentCreator">The document's creator, for self-approval checks.</param>
    /// <param name="locationId">The location scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success when the approval is valid, otherwise the reason it is not.</returns>
    Task<Result> RequireAsync(
        string action,
        decimal absoluteValue,
        UserId approver,
        UserId documentCreator,
        LocationId locationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Allocates human-readable document numbers. The server implementation uses an
/// atomic counter; the device implementation uses a device-scoped counter so a
/// receipt can be printed with no network round-trip.
/// </summary>
public interface IDocumentNumberGenerator
{
    /// <summary>Allocates the next number for a document type.</summary>
    /// <param name="type">The document type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The allocated number.</returns>
    Task<DocumentNumber> NextAsync(DocumentType type, CancellationToken cancellationToken);

    /// <summary>
    /// Allocates the next number for a device-scoped document type, keyed by the
    /// owning device's short code. Browser terminals have no offline number
    /// counter of their own — the server is their single source of truth — so
    /// the server mints their SAL, RET and SHF numbers from the same
    /// <c>core.document_counter</c> table, scoped by device.
    /// </summary>
    /// <param name="type">The device-scoped document type.</param>
    /// <param name="deviceShortCode">The short code of the owning device, for example <c>W02</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The allocated number.</returns>
    Task<DocumentNumber> NextScopedAsync(DocumentType type, string deviceShortCode, CancellationToken cancellationToken);
}

/// <summary>
/// Records the intent to commit, and the boundary within which a business
/// operation is atomic. A sale either fully happened or did not happen.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Persists all pending changes.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of state entries written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Begins an explicit transaction spanning several save operations.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle that commits or rolls back.</returns>
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Gets a value indicating whether a transaction is already open.</summary>
    bool HasActiveTransaction { get; }
}

/// <summary>An open transaction.</summary>
public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    /// <summary>Commits the transaction.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Rolls the transaction back.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the transaction is rolled back.</returns>
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The only way to change stock. Every increase and decrease in the system goes
/// through here; nothing else may write to the balance projection.
/// </summary>
public interface IInventoryLedger
{
    /// <summary>
    /// Validates and posts one inventory event: appends its immutable legs and
    /// applies them to the balance projection, in a single transaction.
    /// </summary>
    /// <param name="spec">The intended event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The posted group, or the reason it was refused.</returns>
    Task<Result<PostedMovementGroup>> PostAsync(MovementGroupSpec spec, CancellationToken cancellationToken);

    /// <summary>Reads the current quantity of one bucket.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="productId">The product.</param>
    /// <param name="batchKey">The batch key, or the empty identifier.</param>
    /// <param name="state">The inventory state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The quantity, or zero when the bucket does not exist.</returns>
    Task<decimal> GetQuantityAsync(
        LocationId locationId,
        ProductId productId,
        BatchId batchKey,
        InventoryState state,
        CancellationToken cancellationToken);
}

/// <summary>
/// Collects the draws the ledger refused for lack of stock during one request,
/// and persists them once the request's transaction has ended.
/// </summary>
/// <remarks>
/// The refusal rolls the command back, so a record staged inside its transaction
/// would vanish with it. The ledger only collects; the pipeline flushes after the
/// unit of work has committed or rolled back (see
/// <c>NegativeStockAttemptBehaviour</c>), through a separate context.
/// </remarks>
public interface INegativeStockAttemptRecorder
{
    /// <summary>Gets a value indicating whether refused draws are waiting to be written.</summary>
    bool HasPending { get; }

    /// <summary>Collects one refused draw. The same event and bucket are recorded once.</summary>
    /// <param name="attempt">The refused draw.</param>
    void Record(NegativeStockAttempt attempt);

    /// <summary>Writes the collected attempts, with their audit entries, and clears them.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the attempts are written.</returns>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of a successful ledger post.</summary>
/// <param name="MovementGroupId">The identifier shared by the posted legs.</param>
/// <param name="EventId">The business event identifier.</param>
/// <param name="LegCount">How many legs were appended.</param>
/// <param name="WasDuplicate">
/// True when the event had already been processed and the stored outcome was
/// replayed instead of posting again.
/// </param>
/// <param name="RecordedAtUtc">The server time the group was recorded.</param>
public sealed record PostedMovementGroup(
    MovementGroupId MovementGroupId,
    EventId EventId,
    int LegCount,
    bool WasDuplicate,
    DateTimeOffset RecordedAtUtc);

/// <summary>
/// Writes an entry to the immutable audit log. Called inside the same
/// transaction as the change it describes.
/// </summary>
public interface IAuditWriter
{
    /// <summary>Records an audited action.</summary>
    /// <param name="entry">The entry to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the entry is staged for commit.</returns>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>One audited action.</summary>
/// <param name="Action">Stable action code, for example <c>transfer.dispatched</c>.</param>
/// <param name="EntityType">The type of the affected record.</param>
/// <param name="EntityId">The identifier of the affected record.</param>
/// <param name="PreviousValueJson">The prior state, scrubbed, or null.</param>
/// <param name="NewValueJson">The new state, scrubbed, or null.</param>
/// <param name="Reason">The reason given by the actor, where one is required.</param>
/// <param name="ReferenceDocumentType">The related document type, if any.</param>
/// <param name="ReferenceDocumentId">The related document identifier, if any.</param>
/// <param name="LocationId">The location the action applies to, if any.</param>
public sealed record AuditEntry(
    string Action,
    string EntityType,
    Guid? EntityId,
    string? PreviousValueJson = null,
    string? NewValueJson = null,
    string? Reason = null,
    ReferenceDocumentType? ReferenceDocumentType = null,
    Guid? ReferenceDocumentId = null,
    LocationId? LocationId = null);
