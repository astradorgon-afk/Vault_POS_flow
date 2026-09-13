using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>
/// A draw the ledger refused because it would have driven a bucket below zero.
/// </summary>
/// <remarks>
/// <para>
/// Nothing was posted, so the ledger holds no trace of the attempt; this record
/// is the trace. Repeated attempts on the same product and location are one of
/// the clearest shrinkage signals the system has: stock that the counters say is
/// not there, being sold or shipped anyway.
/// </para>
/// <para>
/// The record is written after the refused command's transaction has rolled
/// back, and it is append-only under the same guards as the audit log: no
/// setters, the EF interceptor, database triggers, and a role granted only
/// SELECT and INSERT. A shrinkage signal that can be deleted is not one.
/// </para>
/// </remarks>
public sealed class NegativeStockAttempt : Entity<NegativeStockAttemptId>
{
    private NegativeStockAttempt(
        NegativeStockAttemptId id,
        MovementGroupSpec spec,
        LocationId locationId,
        ProductId productId,
        BatchId batchKey,
        InventoryState state,
        decimal requestedQuantity,
        decimal availableQuantity,
        NegativeStockPolicy policy,
        DateTimeOffset attemptedAtUtc)
    {
        Id = id;
        EventId = spec.EventId;
        MovementType = spec.MovementType;
        LocationId = locationId;
        ProductId = productId;
        BatchKey = batchKey;
        State = state;
        RequestedQuantity = requestedQuantity;
        AvailableQuantity = availableQuantity;
        Policy = policy;
        ReferenceDocumentType = spec.ReferenceDocumentType;
        ReferenceDocumentId = spec.ReferenceDocumentId;
        ReferenceNumber = spec.ReferenceNumber;
        UserId = spec.Actor.CreatedBy;
        DeviceId = spec.Actor.Device;
        CorrelationId = spec.Actor.Correlation;
        AttemptedAtUtc = attemptedAtUtc;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private NegativeStockAttempt()
        => ReferenceNumber = string.Empty;

    /// <summary>Gets the business event that was refused.</summary>
    public EventId EventId { get; private init; }

    /// <summary>Gets the kind of movement that was attempted.</summary>
    public InventoryMovementType MovementType { get; private init; }

    /// <summary>Gets the location whose bucket was short.</summary>
    public LocationId LocationId { get; private init; }

    /// <summary>Gets the product that was short.</summary>
    public ProductId ProductId { get; private init; }

    /// <summary>Gets the batch key of the bucket; empty for products not tracked by batch.</summary>
    public BatchId BatchKey { get; private init; }

    /// <summary>Gets the inventory state of the bucket.</summary>
    public InventoryState State { get; private init; }

    /// <summary>Gets the quantity the event tried to draw from the bucket, in base units.</summary>
    public decimal RequestedQuantity { get; private init; }

    /// <summary>Gets the quantity the bucket held at the time.</summary>
    public decimal AvailableQuantity { get; private init; }

    /// <summary>Gets the negative-stock policy that refused the draw.</summary>
    public NegativeStockPolicy Policy { get; private init; }

    /// <summary>Gets the type of the document that caused the attempt.</summary>
    public ReferenceDocumentType ReferenceDocumentType { get; private init; }

    /// <summary>Gets the identifier of that document, if it has one.</summary>
    public Guid? ReferenceDocumentId { get; private init; }

    /// <summary>Gets the human-readable document number quoted on the attempt.</summary>
    public string ReferenceNumber { get; private init; }

    /// <summary>Gets who attempted it.</summary>
    public UserId UserId { get; private init; }

    /// <summary>Gets the device it came from, if any.</summary>
    public DeviceId? DeviceId { get; private init; }

    /// <summary>Gets the correlation identifier of the refused request.</summary>
    public CorrelationId CorrelationId { get; private init; }

    /// <summary>Gets when the ledger refused the draw.</summary>
    public DateTimeOffset AttemptedAtUtc { get; private init; }

    /// <summary>Gets how far short the bucket was.</summary>
    public decimal Shortfall => RequestedQuantity - AvailableQuantity;

    /// <summary>Records a refused draw on one bucket.</summary>
    /// <param name="spec">The refused event.</param>
    /// <param name="locationId">The bucket's location.</param>
    /// <param name="productId">The bucket's product.</param>
    /// <param name="batchKey">The bucket's batch key.</param>
    /// <param name="state">The bucket's state.</param>
    /// <param name="requestedQuantity">The total the event tried to draw from the bucket.</param>
    /// <param name="availableQuantity">What the bucket held.</param>
    /// <param name="policy">The policy that refused it.</param>
    /// <param name="attemptedAtUtc">When the ledger refused it.</param>
    /// <returns>The record.</returns>
    public static NegativeStockAttempt Record(
        MovementGroupSpec spec,
        LocationId locationId,
        ProductId productId,
        BatchId batchKey,
        InventoryState state,
        decimal requestedQuantity,
        decimal availableQuantity,
        NegativeStockPolicy policy,
        DateTimeOffset attemptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return new NegativeStockAttempt(
            NegativeStockAttemptId.New(),
            spec,
            locationId,
            productId,
            batchKey,
            state,
            requestedQuantity,
            availableQuantity,
            policy,
            attemptedAtUtc);
    }
}

/// <summary>Identifies a recorded negative-stock attempt.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct NegativeStockAttemptId(Guid Value) : IStronglyTypedId, IComparable<NegativeStockAttemptId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static NegativeStockAttemptId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="NegativeStockAttemptId"/>.</returns>
    public static NegativeStockAttemptId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(NegativeStockAttemptId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
