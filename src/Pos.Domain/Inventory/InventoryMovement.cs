using Pos.Domain.Common;
using Pos.Domain.Sync;

namespace Pos.Domain.Inventory;

/// <summary>
/// One leg of one inventory event: a signed quantity applied to exactly one
/// bucket of (location, product, batch, state).
/// </summary>
/// <remarks>
/// <para>
/// This type is append-only and immutable. It has no setters, and instances are
/// produced only by <see cref="InventoryMovementGroup.Create"/>, which enforces
/// the zero-sum and state-transition invariants. Nothing in the system updates
/// or deletes a movement; corrections are new, reversing groups.
/// </para>
/// <para>
/// Immutability is defended in four independent layers: this type, an EF Core
/// interceptor, database triggers, and the application database role having
/// only SELECT and INSERT on the table.
/// </para>
/// </remarks>
public sealed class InventoryMovement : Entity<InventoryMovementId>
{
    internal InventoryMovement(
        InventoryMovementId id,
        EventId eventId,
        MovementGroupId movementGroupId,
        short legNumber,
        ProductId productId,
        BatchId? batchId,
        LocationId locationId,
        InventoryState state,
        decimal quantityDelta,
        decimal unitCost,
        decimal totalValueDelta,
        InventoryMovementType movementType,
        LocationId? sourceLocationId,
        LocationId? destinationLocationId,
        InventoryState? sourceState,
        InventoryState? destinationState,
        ReferenceDocumentType referenceDocumentType,
        Guid? referenceDocumentId,
        string referenceNumber,
        UserId createdByUserId,
        UserId? approvedByUserId,
        DeviceId? deviceId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset recordedAtUtc,
        DateOnly businessDate,
        AdjustmentReasonCode? reasonCode,
        string? notes,
        MovementGroupId? reversesMovementGroupId,
        SyncStatus syncStatus,
        ServerProcessingStatus serverProcessingStatus,
        CorrelationId correlationId)
    {
        Id = id;
        EventId = eventId;
        MovementGroupId = movementGroupId;
        LegNumber = legNumber;
        ProductId = productId;
        BatchId = batchId;
        LocationId = locationId;
        State = state;
        QuantityDelta = quantityDelta;
        UnitCost = unitCost;
        TotalValueDelta = totalValueDelta;
        MovementType = movementType;
        SourceLocationId = sourceLocationId;
        DestinationLocationId = destinationLocationId;
        SourceState = sourceState;
        DestinationState = destinationState;
        ReferenceDocumentType = referenceDocumentType;
        ReferenceDocumentId = referenceDocumentId;
        ReferenceNumber = referenceNumber;
        CreatedByUserId = createdByUserId;
        ApprovedByUserId = approvedByUserId;
        DeviceId = deviceId;
        OccurredAtUtc = occurredAtUtc;
        RecordedAtUtc = recordedAtUtc;
        BusinessDate = businessDate;
        ReasonCode = reasonCode;
        Notes = notes;
        ReversesMovementGroupId = reversesMovementGroupId;
        SyncStatus = syncStatus;
        ServerProcessingStatus = serverProcessingStatus;
        CorrelationId = correlationId;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private InventoryMovement()
    {
        ReferenceNumber = string.Empty;
    }

    /// <summary>Gets the globally unique identifier of the business event this leg belongs to.</summary>
    public EventId EventId { get; private init; }

    /// <summary>Gets the identifier shared by every leg of this event.</summary>
    public MovementGroupId MovementGroupId { get; private init; }

    /// <summary>Gets the ordinal of this leg within its group, starting at one.</summary>
    public short LegNumber { get; private init; }

    /// <summary>Gets the product affected.</summary>
    public ProductId ProductId { get; private init; }

    /// <summary>Gets the batch affected, when the product is batch-tracked.</summary>
    public BatchId? BatchId { get; private init; }

    /// <summary>Gets the location whose bucket this leg changes.</summary>
    public LocationId LocationId { get; private init; }

    /// <summary>Gets the inventory state whose bucket this leg changes.</summary>
    public InventoryState State { get; private init; }

    /// <summary>Gets the signed change in quantity. Never zero.</summary>
    public decimal QuantityDelta { get; private init; }

    /// <summary>Gets the valuation cost per unit applied to this leg.</summary>
    public decimal UnitCost { get; private init; }

    /// <summary>Gets the signed change in inventory value, equal to cost times quantity.</summary>
    public decimal TotalValueDelta { get; private init; }

    /// <summary>Gets the reason stock moved.</summary>
    public InventoryMovementType MovementType { get; private init; }

    /// <summary>Gets the event-level source location, for reporting and traceability.</summary>
    public LocationId? SourceLocationId { get; private init; }

    /// <summary>Gets the event-level destination location, for reporting and traceability.</summary>
    public LocationId? DestinationLocationId { get; private init; }

    /// <summary>Gets the event-level source state.</summary>
    public InventoryState? SourceState { get; private init; }

    /// <summary>Gets the event-level destination state.</summary>
    public InventoryState? DestinationState { get; private init; }

    /// <summary>Gets the kind of document that justified the movement.</summary>
    public ReferenceDocumentType ReferenceDocumentType { get; private init; }

    /// <summary>Gets the identifier of the justifying document.</summary>
    public Guid? ReferenceDocumentId { get; private init; }

    /// <summary>Gets the human-readable document number, for example TRF-2026-000001.</summary>
    public string ReferenceNumber { get; private init; }

    /// <summary>Gets the user who created the movement.</summary>
    public UserId CreatedByUserId { get; private init; }

    /// <summary>Gets the user who approved it, where an approval was required.</summary>
    public UserId? ApprovedByUserId { get; private init; }

    /// <summary>Gets the device the movement originated from, if any.</summary>
    public DeviceId? DeviceId { get; private init; }

    /// <summary>Gets the business time the movement happened, as reported by its origin.</summary>
    public DateTimeOffset OccurredAtUtc { get; private init; }

    /// <summary>Gets the server clock time the movement was recorded. Authoritative for ordering.</summary>
    public DateTimeOffset RecordedAtUtc { get; private init; }

    /// <summary>Gets the trading day the movement belongs to, in the location's timezone.</summary>
    public DateOnly BusinessDate { get; private init; }

    /// <summary>Gets the adjustment reason, where the movement type requires one.</summary>
    public AdjustmentReasonCode? ReasonCode { get; private init; }

    /// <summary>Gets free-text notes recorded with the movement.</summary>
    public string? Notes { get; private init; }

    /// <summary>Gets the group this movement reverses, when it is a correction.</summary>
    public MovementGroupId? ReversesMovementGroupId { get; private init; }

    /// <summary>Gets the upload state, for movements created on a device.</summary>
    public SyncStatus SyncStatus { get; private init; }

    /// <summary>Gets the server's verdict on the originating event.</summary>
    public ServerProcessingStatus ServerProcessingStatus { get; private init; }

    /// <summary>Gets the identifier correlating this movement with the wider operation.</summary>
    public CorrelationId CorrelationId { get; private init; }

    /// <summary>Gets a value indicating whether this leg increases the bucket.</summary>
    public bool IsIncrease => QuantityDelta > 0m;

    /// <summary>
    /// Gets the batch key used by the balance projection, which substitutes the
    /// empty identifier for products that are not batch-tracked so the primary
    /// key stays non-nullable.
    /// </summary>
    public BatchId BatchKey => BatchId ?? Common.BatchId.Empty;
}
