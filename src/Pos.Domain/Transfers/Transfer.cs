using Pos.Domain.Common;

namespace Pos.Domain.Transfers;

/// <summary>The lifecycle state of a transfer order.</summary>
public enum TransferStatus
{
    /// <summary>The transfer is being prepared and can still be changed.</summary>
    Draft = 1,

    /// <summary>The transfer awaits review by an approver.</summary>
    Submitted = 2,

    /// <summary>An approver is examining the request; submission can still be rejected.</summary>
    InReview = 3,

    /// <summary>The transfer is approved and ready to be picked.</summary>
    Approved = 4,

    /// <summary>Stock is being picked at the source location.</summary>
    Picking = 5,

    /// <summary>Picking is complete; the transfer waits to leave the source.</summary>
    Ready = 6,

    /// <summary>The stock has left the source: the ledger has been posted.</summary>
    Dispatched = 7,

    /// <summary>Part of the dispatch arrived; some is short or damaged.</summary>
    PartiallyReceived = 8,

    /// <summary>Everything that was dispatched has arrived (or been accounted for).</summary>
    Received = 9,

    /// <summary>Every arrival discrepancy is resolved and the transfer is done.</summary>
    Closed = 10,

    /// <summary>The dispatch was cancelled before the destination received any of it.</summary>
    Cancelled = 11,
}

/// <summary>What kind of arrival discrepancy a transfer records.</summary>
public enum TransferDiscrepancyKind
{
    /// <summary>More stock was dispatched than arrived.</summary>
    Shortage = 1,
}

/// <summary>How a shortage was ultimately accounted for.</summary>
public enum TransferDiscrepancyResolutionOutcome
{
    /// <summary>The missing stock was found and returned to available stock.</summary>
    Found = 1,

    /// <summary>The missing stock is unrecoverable and written off.</summary>
    WriteOff = 2,
}

/// <summary>What a custody event records on a transfer's audit timeline.</summary>
public enum TransferCustodyEventKind
{
    /// <summary>The transfer was raised.</summary>
    Created = 1,

    /// <summary>The transfer was submitted for review.</summary>
    Submitted = 2,

    /// <summary>An approver started reviewing the transfer.</summary>
    Reviewed = 3,

    /// <summary>An approver approved the transfer and an amendment was applied.</summary>
    ApprovedWithAmendment = 4,

    /// <summary>An approver approved the transfer.</summary>
    Approved = 5,

    /// <summary>The transfer was sent back to draft for rework.</summary>
    Rejected = 6,

    /// <summary>Stock was picked at the source location.</summary>
    Picked = 7,

    /// <summary>The stock left the source location.</summary>
    Dispatched = 8,

    /// <summary>A dispatched transfer was cancelled before arrival.</summary>
    DispatchCancelled = 9,

    /// <summary>The stock arrived at the destination, in full or in part.</summary>
    Received = 10,

    /// <summary>An arrival discrepancy was resolved.</summary>
    Resolved = 11,

    /// <summary>The transfer was verified and closed.</summary>
    Verified = 12,
}

/// <summary>One requested line of a transfer.</summary>
/// <param name="ProductId">The product to move.</param>
/// <param name="RequestedQuantity">The quantity requested, in the product's base unit.</param>
/// <param name="Note">An optional note for the picking team.</param>
public sealed record TransferLineSpec(
    ProductId ProductId,
    decimal RequestedQuantity,
    string? Note = null);

/// <summary>An approval-time change to a requested quantity.</summary>
/// <param name="LineNo">The line to change.</param>
/// <param name="RequestedQuantity">The new requested quantity.</param>
public sealed record TransferLineAmendment(
    int LineNo,
    decimal RequestedQuantity);

/// <summary>One picking allocation: a concrete lot and quantity snapped at pick time.</summary>
/// <param name="LineNo">The transfer line the allocation satisfies.</param>
/// <param name="BatchId">The specific lot, when the product is batch-tracked.</param>
/// <param name="Quantity">The quantity picked, in the base unit.</param>
/// <param name="UnitCost">The snapshot unit cost at pick time, used to value the dispatch.</param>
public sealed record TransferPickAllocationSpec(
    int LineNo,
    BatchId? BatchId,
    decimal Quantity,
    decimal UnitCost);

/// <summary>One arrival receipt line, keyed to the allocation that was dispatched.</summary>
/// <param name="LineNo">The transfer line being receipted.</param>
/// <param name="BatchId">The lot as picked; required when the product is batch-tracked.</param>
/// <param name="ReceivedQuantity">The quantity that arrived in good condition.</param>
/// <param name="DamagedQuantity">The quantity that arrived damaged.</param>
public sealed record TransferReceiveAllocationSpec(
    int LineNo,
    BatchId? BatchId,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);

/// <summary>A requested line of a transfer order.</summary>
public sealed class TransferLine : Entity<TransferOrderLineId>
{
    internal TransferLine(
        TransferOrderLineId id,
        TransferOrderId transferOrderId,
        int lineNo,
        ProductId productId,
        decimal requestedQuantity,
        string? note)
    {
        Id = id;
        TransferOrderId = transferOrderId;
        LineNo = lineNo;
        ProductId = productId;
        RequestedQuantity = requestedQuantity;
        Note = note;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private TransferLine()
    {
        ProductId = ProductId.Empty;
    }

    /// <summary>Gets the owning transfer.</summary>
    public TransferOrderId TransferOrderId { get; private set; }

    /// <summary>Gets the line number, for display.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the product to move.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the requested quantity, in the base unit.</summary>
    public decimal RequestedQuantity { get; private set; }

    /// <summary>Gets the optional picking note.</summary>
    public string? Note { get; private set; }

    /// <summary>Applies an approval amendment.</summary>
    internal void Amend(decimal requestedQuantity) => RequestedQuantity = requestedQuantity;
}

/// <summary>A picked quantity of a concrete lot, snapped at pick time.</summary>
public sealed class TransferPickAllocation : Entity<TransferAllocationId>
{
    internal TransferPickAllocation(
        TransferAllocationId id,
        TransferOrderId transferOrderId,
        int lineNo,
        BatchId? batchId,
        decimal quantity,
        decimal unitCost)
    {
        Id = id;
        TransferOrderId = transferOrderId;
        LineNo = lineNo;
        BatchId = batchId;
        Quantity = quantity;
        UnitCost = unitCost;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private TransferPickAllocation()
    {
    }

    /// <summary>Gets the owning transfer.</summary>
    public TransferOrderId TransferOrderId { get; private set; }

    /// <summary>Gets the transfer line this allocation satisfies.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the specific lot picked, when batch-tracked.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>Gets the quantity picked, in the base unit.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the snapshot unit cost at pick time.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets the quantity that arrived in good condition.</summary>
    public decimal ReceivedQuantity { get; private set; }

    /// <summary>Gets the quantity that arrived damaged.</summary>
    public decimal DamagedQuantity { get; private set; }

    /// <summary>Gets whether the destination has accounted for the whole allocation.</summary>
    public bool FullyAccounted => ReceivedQuantity + DamagedQuantity == Quantity;

    /// <summary>Gets the shortfall between what was dispatched and what arrived.</summary>
    public decimal Shortfall => Quantity - ReceivedQuantity - DamagedQuantity;

    /// <summary>Gets the value of the allocation at the picked cost.</summary>
    public decimal LineTotal => Quantity * UnitCost;

    /// <summary>Records what arrived against this allocation.</summary>
    internal void RecordArrival(decimal receivedQuantity, decimal damagedQuantity)
    {
        ReceivedQuantity = receivedQuantity;
        DamagedQuantity = damagedQuantity;
    }
}

/// <summary>An arrival shortage awaiting resolution.</summary>
public sealed class TransferDiscrepancy : Entity<TransferDiscrepancyId>
{
    internal TransferDiscrepancy(
        TransferDiscrepancyId id,
        TransferOrderId transferOrderId,
        int lineNo,
        BatchId? batchId,
        decimal quantity)
    {
        Id = id;
        TransferOrderId = transferOrderId;
        LineNo = lineNo;
        BatchId = batchId;
        Quantity = quantity;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private TransferDiscrepancy()
    {
    }

    /// <summary>Gets the owning transfer.</summary>
    public TransferOrderId TransferOrderId { get; private set; }

    /// <summary>Gets the transfer line the shortage belongs to.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the lot that was short, when batch-tracked.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>Gets how stock can go missing on a transfer: a quantity shortfall.</summary>
    public TransferDiscrepancyKind Kind { get; private set; } = TransferDiscrepancyKind.Shortage;

    /// <summary>Gets the short quantity, in the base unit.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the resolution outcome, once resolved.</summary>
    public TransferDiscrepancyResolutionOutcome? ResolutionOutcome { get; private set; }

    /// <summary>Gets who resolved the discrepancy, once resolved.</summary>
    public UserId? ResolvedByUserId { get; private set; }

    /// <summary>Gets when the discrepancy was resolved, once resolved.</summary>
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>Gets the resolution note, when one was given.</summary>
    public string? ResolutionNote { get; private set; }

    /// <summary>Gets whether the discrepancy is closed.</summary>
    public bool IsResolved => ResolutionOutcome is not null;

    /// <summary>Resolves the discrepancy.</summary>
    internal void Resolve(
        TransferDiscrepancyResolutionOutcome outcome,
        UserId resolvedByUserId,
        DateTimeOffset resolvedAtUtc,
        string? note)
    {
        ResolutionOutcome = outcome;
        ResolvedByUserId = resolvedByUserId;
        ResolvedAtUtc = resolvedAtUtc;
        ResolutionNote = note?.Trim();
    }
}

/// <summary>One step in a transfer's custody timeline, for the audit record.</summary>
public sealed class TransferCustodyEvent : Entity<TransferCustodyEventId>
{
    internal TransferCustodyEvent(
        TransferCustodyEventId id,
        TransferOrderId transferOrderId,
        int sequence,
        TransferCustodyEventKind kind,
        UserId actorUserId,
        DateTimeOffset occurredAtUtc,
        string? note)
    {
        Id = id;
        TransferOrderId = transferOrderId;
        Sequence = sequence;
        Kind = kind;
        ActorUserId = actorUserId;
        OccurredAtUtc = occurredAtUtc;
        Note = note;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private TransferCustodyEvent()
    {
        ActorUserId = UserId.Empty;
    }

    /// <summary>Gets the owning transfer.</summary>
    public TransferOrderId TransferOrderId { get; private set; }

    /// <summary>Gets the event's 1-based position in the timeline.</summary>
    public int Sequence { get; private set; }

    /// <summary>Gets what happened.</summary>
    public TransferCustodyEventKind Kind { get; private set; }

    /// <summary>Gets the actor.</summary>
    public UserId ActorUserId { get; private set; }

    /// <summary>Gets when it happened.</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Gets the optional note.</summary>
    public string? Note { get; private set; }
}

/// <summary>
/// A transfer order: stock moves from a main warehouse to a store, or between
/// stores, posting <c>Available@source −q → InTransit@source +q</c> on dispatch
/// and <c>InTransit@source −q → Available/Damaged/TransitVariance@destination +q</c>
/// on arrival. The TRF number is allocated at dispatch, after picking, so a failed
/// dispatch can never burn a sequence.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle is Draft → Submitted → InReview → Approved → Picking → Ready →
/// Dispatched → Received/PartiallyReceived → Closed. Rejection returns the
/// transfer to Draft for rework. At arrival the destination may receive less than
/// was dispatched; the shortfall becomes a <see cref="TransferDiscrepancy"/> that
/// is later either found (returned to available) or written off.
/// </para>
/// <para>
/// Approval happens against the requested quantities, before costs are known, so
/// the approval gate is evaluated against the estimate the handler computes from
/// product master costs. Once picked, the dispatch is valued at the snapped unit
/// costs. Cancelling a dispatch returns stock to available at the source and
/// requires a second authority: the ledger rule marks it an approved movement.
/// </para>
/// </remarks>
public sealed class Transfer : AggregateRoot<TransferOrderId>
{
    private readonly List<TransferLine> _lines = [];
    private readonly List<TransferPickAllocation> _allocations = [];
    private readonly List<TransferDiscrepancy> _discrepancies = [];
    private readonly List<TransferCustodyEvent> _custodyEvents = [];

    private Transfer(
        TransferOrderId id,
        LocationId sourceLocationId,
        LocationId destinationLocationId,
        IReadOnlyList<TransferLine> lines,
        UserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        SourceLocationId = sourceLocationId;
        DestinationLocationId = destinationLocationId;
        _lines.AddRange(lines);
        CreatedByUserId = createdBy;
        CreatedAtUtc = createdAtUtc;
        Status = TransferStatus.Draft;
        _custodyEvents.Add(new TransferCustodyEvent(
            TransferCustodyEventId.New(),
            id,
            _custodyEvents.Count + 1,
            TransferCustodyEventKind.Created,
            createdBy,
            createdAtUtc,
            null));
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Transfer()
    {
        SourceLocationId = LocationId.Empty;
        DestinationLocationId = LocationId.Empty;
        CreatedByUserId = UserId.Empty;
        Number = string.Empty;
    }

    /// <summary>Creates a draft transfer between two locations.</summary>
    /// <param name="sourceLocationId">The location the stock leaves.</param>
    /// <param name="destinationLocationId">The location the stock is destined for.</param>
    /// <param name="specs">The lines to move.</param>
    /// <param name="createdBy">The user raising the transfer.</param>
    /// <param name="createdAtUtc">The current instant.</param>
    /// <returns>The draft transfer, or validation errors.</returns>
    public static Result<Transfer> Create(
        LocationId sourceLocationId,
        LocationId destinationLocationId,
        IReadOnlyList<TransferLineSpec> specs,
        UserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        if (sourceLocationId.IsEmpty)
        {
            return Result<Transfer>.Failure(TransferErrors.SourceRequired);
        }

        if (destinationLocationId.IsEmpty)
        {
            return Result<Transfer>.Failure(TransferErrors.DestinationRequired);
        }

        if (sourceLocationId == destinationLocationId)
        {
            return Result<Transfer>.Failure(TransferErrors.SameLocation);
        }

        if (specs is null || specs.Count == 0)
        {
            return Result<Transfer>.Failure(TransferErrors.EmptyTransfer);
        }

        List<Error> errors = [];
        List<TransferLine> lines = [];
        TransferOrderId transferId = TransferOrderId.New();

        for (int i = 0; i < specs.Count; i++)
        {
            TransferLineSpec spec = specs[i];
            int lineNo = i + 1;

            if (spec.RequestedQuantity <= 0m)
            {
                errors.Add(TransferErrors.LineQuantityInvalid(lineNo));
            }

            bool duplicated = lines.Any(l =>
                l.ProductId == spec.ProductId
                && l.Note == spec.Note?.Trim());

            if (duplicated)
            {
                errors.Add(TransferErrors.DuplicateLine(lineNo));
            }
            else
            {
                lines.Add(new TransferLine(
                    TransferOrderLineId.New(),
                    transferId,
                    lineNo,
                    spec.ProductId,
                    spec.RequestedQuantity,
                    spec.Note?.Trim()));
            }
        }

        return errors.Count == 0
            ? Result<Transfer>.Success(new Transfer(
                transferId,
                sourceLocationId,
                destinationLocationId,
                lines,
                createdBy,
                createdAtUtc))
            : Result<Transfer>.Failure(errors);
    }

    /// <summary>Submits the transfer for review.</summary>
    /// <param name="submittedBy">The user submitting the transfer.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Submit(UserId submittedBy, DateTimeOffset now)
    {
        if (Status != TransferStatus.Draft)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.Draft, Status));
        }

        Status = TransferStatus.Submitted;
        _custodyEvents.Add(Event(TransferCustodyEventKind.Submitted, submittedBy, now, null));
        return Result.Success();
    }

    /// <summary>Marks the transfer as being reviewed.</summary>
    /// <param name="reviewer">The reviewing user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">An optional review note.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Review(UserId reviewer, DateTimeOffset now, string? note)
    {
        if (Status != TransferStatus.Submitted)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.Submitted, Status));
        }

        Status = TransferStatus.InReview;
        _custodyEvents.Add(Event(TransferCustodyEventKind.Reviewed, reviewer, now, note?.Trim()));
        return Result.Success();
    }

    /// <summary>
    /// Approves the transfer, optionally applying approval-time quantity
    /// amendments. The amended quantities govern what may be picked.
    /// </summary>
    /// <param name="approver">The approving user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="amendments">Optional quantity amendments.</param>
    /// <param name="note">An optional approval note.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Approve(
        UserId approver,
        DateTimeOffset now,
        IReadOnlyList<TransferLineAmendment>? amendments = null,
        string? note = null)
    {
        if (Status != TransferStatus.InReview)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.InReview, Status));
        }

        if (amendments is not null && amendments.Count > 0)
        {
            List<Error> errors = [];
            HashSet<int> seen = [];

            foreach (TransferLineAmendment amendment in amendments)
            {
                TransferLine? line = _lines.FirstOrDefault(l => l.LineNo == amendment.LineNo);

                if (line is null || amendment.RequestedQuantity <= 0m || !seen.Add(amendment.LineNo))
                {
                    errors.Add(TransferErrors.AmendmentInvalid(amendment.LineNo));
                    continue;
                }

                line.Amend(amendment.RequestedQuantity);
            }

            if (errors.Count > 0)
            {
                return Result.Failure(errors);
            }

            _custodyEvents.Add(Event(TransferCustodyEventKind.ApprovedWithAmendment, approver, now, note?.Trim()));
        }
        else
        {
            _custodyEvents.Add(Event(TransferCustodyEventKind.Approved, approver, now, note?.Trim()));
        }

        Status = TransferStatus.Approved;
        ApprovedByUserId = approver;
        ApprovedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Rejects the transfer, returning it to draft for rework.</summary>
    /// <param name="reviewer">The reviewing user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">The reason for rejection.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Reject(UserId reviewer, DateTimeOffset now, string? note)
    {
        if (Status is not TransferStatus.Submitted and not TransferStatus.InReview)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(
                new[] { TransferStatus.Submitted, TransferStatus.InReview }, Status));
        }

        Status = TransferStatus.Draft;
        _custodyEvents.Add(Event(TransferCustodyEventKind.Rejected, reviewer, now, note?.Trim()));
        return Result.Success();
    }

    /// <summary>
    /// Records what was picked at the source. The picking team decides the lots;
    /// the handler enforces first-expiry-first-out and that only available stock
    /// is picked. Allocations are snapshotted with their unit cost.
    /// </summary>
    /// <param name="picker">The picking user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="specs">The picked allocations.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Pick(
        UserId picker,
        DateTimeOffset now,
        IReadOnlyList<TransferPickAllocationSpec> specs)
    {
        if (Status != TransferStatus.Approved)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.Approved, Status));
        }

        if (specs is null || specs.Count == 0)
        {
            return Result.Failure(TransferErrors.NothingPicked);
        }

        List<Error> errors = [];
        Dictionary<(int LineNo, BatchId BatchKey), bool> seen = [];

        foreach (TransferPickAllocationSpec spec in specs)
        {
            TransferLine? line = _lines.FirstOrDefault(l => l.LineNo == spec.LineNo);
            BatchId batchKey = spec.BatchId is null ? BatchId.Empty : spec.BatchId.Value;

            if (line is null)
            {
                errors.Add(TransferErrors.LineUnknown(spec.LineNo));
                continue;
            }

            if (spec.Quantity <= 0m)
            {
                errors.Add(TransferErrors.PickQuantityInvalid(spec.LineNo));
            }

            decimal lineTotal = specs.Where(s => s.LineNo == spec.LineNo).Sum(s => s.Quantity);

            if (lineTotal > line.RequestedQuantity)
            {
                errors.Add(TransferErrors.PickExceedsRequested(spec.LineNo));
            }

            if (!seen.TryAdd((spec.LineNo, batchKey), true))
            {
                errors.Add(TransferErrors.DuplicateAllocation(spec.LineNo));
            }
        }

        if (errors.Count > 0)
        {
            return Result.Failure(errors);
        }

        foreach (TransferPickAllocationSpec spec in specs)
        {
            _allocations.Add(new TransferPickAllocation(
                TransferAllocationId.New(),
                Id,
                spec.LineNo,
                spec.BatchId,
                spec.Quantity,
                spec.UnitCost));
        }

        Status = TransferStatus.Picking;
        PickedByUserId = picker;
        PickedAtUtc = now;
        _custodyEvents.Add(Event(TransferCustodyEventKind.Picked, picker, now, null));
        return Result.Success();
    }

    /// <summary>Marks picking complete; the transfer may now be dispatched.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Ready(DateTimeOffset now)
    {
        if (Status != TransferStatus.Picking)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.Picking, Status));
        }

        if (_allocations.Count == 0)
        {
            return Result.Failure(TransferErrors.NothingPicked);
        }

        Status = TransferStatus.Ready;
        return Result.Success();
    }

    /// <summary>
    /// Dispatches the transfer: records the TRF number, the SHP shipment number
    /// and opens the ledger's in-transit buckets. The handler posts the ledger
    /// group after this succeeds; a failed post rolls the whole transaction back.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="number">The allocated TRF number.</param>
    /// <param name="shipmentId">The shipment identifier used as the ledger reference.</param>
    /// <param name="shipmentNumber">The human-readable SHP number.</param>
    /// <param name="dispatcher">The user dispatching the stock.</param>
    /// <returns>Success, or a state or conflict error.</returns>
    public Result Dispatch(
        DateTimeOffset now,
        DocumentNumber number,
        TransferShipmentId shipmentId,
        string shipmentNumber,
        UserId dispatcher)
    {
        // The number guard precedes the state guard: a transfer that already
        // carries a number must never be dispatched again, whatever its state.
        if (Number.Length > 0)
        {
            return Result.Failure(TransferErrors.AlreadyNumbered(Id));
        }

        if (Status != TransferStatus.Ready)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.Ready, Status));
        }

        Number = number.Value;
        ShipmentId = shipmentId;
        ShipmentNumber = shipmentNumber;
        DispatchedByUserId = dispatcher;
        DispatchedAtUtc = now;
        Status = TransferStatus.Dispatched;
        _custodyEvents.Add(Event(TransferCustodyEventKind.Dispatched, dispatcher, now, null));
        return Result.Success();
    }

    /// <summary>
    /// Cancels a dispatched transfer before the destination receives any of it.
    /// The stock returns to available at the source; the handler posts the
    /// reverse ledger group, which the rules engine treats as an approved,
    /// reason-bearing movement.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="reason">Why the dispatch was undone; recorded on the ledger.</param>
    /// <param name="canceller">The user cancelling the dispatch.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result CancelDispatch(DateTimeOffset now, string reason, UserId canceller)
    {
        // Arrivals are checked before the state guard: any received quantity
        // means the destination took custody, whatever the recorded status.
        if (_allocations.Any(a => a.ReceivedQuantity > 0m || a.DamagedQuantity > 0m))
        {
            return Result.Failure(TransferErrors.CancelAfterReceive);
        }

        if (Status != TransferStatus.Dispatched)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.Dispatched, Status));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(TransferErrors.CancelReasonRequired);
        }

        Status = TransferStatus.Cancelled;
        CancelReason = reason.Trim();
        CancelledByUserId = canceller;
        CancelledAtUtc = now;
        _custodyEvents.Add(Event(TransferCustodyEventKind.DispatchCancelled, canceller, now, reason.Trim()));
        return Result.Success();
    }

    /// <summary>
    /// Records what arrived. Each allocation is receipted against what was
    /// picked; over-receipt is refused because the ledger would have nothing to
    /// draw from. Shortfalls become <see cref="TransferDiscrepancy"/> records to
    /// be resolved by the reconciliation workflow. The handler posts the ledger
    /// group (moving the in-transit stock) after this succeeds.
    /// </summary>
    /// <param name="receiver">The user receipting the stock.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="receiptId">The receipt identifier used as the ledger reference.</param>
    /// <param name="receiptNumber">The human-readable TRC number.</param>
    /// <param name="receives">What arrived, per picked allocation.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Receive(
        UserId receiver,
        DateTimeOffset now,
        TransferReceiptId receiptId,
        string receiptNumber,
        IReadOnlyList<TransferReceiveAllocationSpec> receives)
    {
        if (Status != TransferStatus.Dispatched)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(TransferStatus.Dispatched, Status));
        }

        if (receives is null || receives.Count == 0)
        {
            return Result.Failure(TransferErrors.NothingReceived);
        }

        List<Error> errors = [];
        List<(TransferPickAllocation Allocation, TransferReceiveAllocationSpec Spec)> arrivals = [];

        foreach (TransferReceiveAllocationSpec spec in receives)
        {
            TransferPickAllocation? allocation = _allocations.FirstOrDefault(a =>
                a.LineNo == spec.LineNo
                && (a.BatchId is null ? BatchId.Empty : a.BatchId.Value) == (spec.BatchId is null ? BatchId.Empty : spec.BatchId.Value));

            if (allocation is null)
            {
                errors.Add(TransferErrors.ReceiveUnknownAllocation(spec.LineNo));
                continue;
            }

            if (spec.ReceivedQuantity < 0m || spec.DamagedQuantity < 0m)
            {
                errors.Add(TransferErrors.ReceiveQuantityInvalid(spec.LineNo));
            }

            decimal arrived = spec.ReceivedQuantity + spec.DamagedQuantity;

            if (arrived > allocation.Quantity)
            {
                errors.Add(TransferErrors.ReceiveExceedsAllocation(spec.LineNo));
            }

            arrivals.Add((allocation, spec));
        }

        if (errors.Count > 0)
        {
            return Result.Failure(errors);
        }

        foreach ((TransferPickAllocation allocation, TransferReceiveAllocationSpec spec) in arrivals)
        {
            allocation.RecordArrival(spec.ReceivedQuantity, spec.DamagedQuantity);

            decimal shortfall = allocation.Shortfall;

            if (shortfall > 0m)
            {
                _discrepancies.Add(new TransferDiscrepancy(
                    TransferDiscrepancyId.New(),
                    Id,
                    spec.LineNo,
                    spec.BatchId,
                    shortfall));
            }
        }

        ReceiptId = receiptId;
        ReceiptNumber = receiptNumber;
        ReceivedByUserId = receiver;
        ReceivedAtUtc = now;
        Status = _allocations.All(a => a.FullyAccounted)
            ? TransferStatus.Received
            : TransferStatus.PartiallyReceived;
        _custodyEvents.Add(Event(TransferCustodyEventKind.Received, receiver, now, null));
        return Result.Success();
    }

    /// <summary>
    /// Resolves an arrival discrepancy: the short stock was either found at the
    /// destination (returned to available) or written off. The handler posts the
    /// corresponding ledger group, which the rules engine treats as an approved,
    /// reason-bearing movement.
    /// </summary>
    /// <param name="discrepancyId">The discrepancy to resolve.</param>
    /// <param name="outcome">Whether the stock was found or written off.</param>
    /// <param name="resolver">The reconciling user.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="note">An optional resolution note.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result ResolveDiscrepancy(
        TransferDiscrepancyId discrepancyId,
        TransferDiscrepancyResolutionOutcome outcome,
        UserId resolver,
        DateTimeOffset now,
        string? note)
    {
        TransferDiscrepancy? discrepancy = _discrepancies.FirstOrDefault(d => d.Id == discrepancyId);

        if (discrepancy is null)
        {
            return Result.Failure(TransferErrors.DiscrepancyUnknown(discrepancyId));
        }

        if (discrepancy.IsResolved)
        {
            return Result.Failure(TransferErrors.DiscrepancyAlreadyResolved(discrepancyId));
        }

        discrepancy.Resolve(outcome, resolver, now, note);
        _custodyEvents.Add(Event(TransferCustodyEventKind.Resolved, resolver, now, note?.Trim()));
        return Result.Success();
    }

    /// <summary>
    /// Verifies and closes the transfer: every arrival discrepancy is resolved
    /// and at least some stock arrived. No ledger movement is posted for this —
    /// the buckets are already correct after the resolution groups.
    /// </summary>
    /// <param name="verifier">The verifying user.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or conflict error.</returns>
    public Result Verify(UserId verifier, DateTimeOffset now)
    {
        if (Status is not TransferStatus.Received and not TransferStatus.PartiallyReceived)
        {
            return Result.Failure(TransferErrors.InvalidTransferState(
                new[] { TransferStatus.Received, TransferStatus.PartiallyReceived }, Status));
        }

        if (_discrepancies.Any(d => !d.IsResolved))
        {
            return Result.Failure(TransferErrors.VerifyHasOpenVariance);
        }

        if (_allocations.All(a => a.ReceivedQuantity == 0m))
        {
            return Result.Failure(TransferErrors.VerifyNothingReceived);
        }

        Status = TransferStatus.Closed;
        VerifiedByUserId = verifier;
        VerifiedAtUtc = now;
        _custodyEvents.Add(Event(TransferCustodyEventKind.Verified, verifier, now, null));
        return Result.Success();
    }

    /// <summary>Appends a custody event at the next sequence position.</summary>
    private TransferCustodyEvent Event(
        TransferCustodyEventKind kind,
        UserId actor,
        DateTimeOffset occurredAtUtc,
        string? note) => new(
            TransferCustodyEventId.New(),
            Id,
            _custodyEvents.Count + 1,
            kind,
            actor,
            occurredAtUtc,
            note);

    /// <summary>Gets the lifecycle status.</summary>
    public TransferStatus Status { get; private set; }

    /// <summary>Gets the TRF document number, allocated at dispatch.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>Gets the location the stock leaves.</summary>
    public LocationId SourceLocationId { get; private set; }

    /// <summary>Gets the location the stock is destined for.</summary>
    public LocationId DestinationLocationId { get; private set; }

    /// <summary>Gets the user who raised the transfer.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets the instant the transfer was raised.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the user who approved the transfer, when approved.</summary>
    public UserId? ApprovedByUserId { get; private set; }

    /// <summary>Gets the instant the transfer was approved, when approved.</summary>
    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    /// <summary>Gets the user who picked the stock, when picked.</summary>
    public UserId? PickedByUserId { get; private set; }

    /// <summary>Gets the instant picking was recorded.</summary>
    public DateTimeOffset? PickedAtUtc { get; private set; }

    /// <summary>Gets the user who dispatched the stock, when dispatched.</summary>
    public UserId? DispatchedByUserId { get; private set; }

    /// <summary>Gets the instant the stock left the source.</summary>
    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    /// <summary>Gets the shipment identifier used as the dispatch ledger reference.</summary>
    public TransferShipmentId? ShipmentId { get; private set; }

    /// <summary>Gets the human-readable SHP shipment number.</summary>
    public string? ShipmentNumber { get; private set; }

    /// <summary>Gets who cancelled the dispatch, when cancelled.</summary>
    public UserId? CancelledByUserId { get; private set; }

    /// <summary>Gets the instant the dispatch was cancelled.</summary>
    public DateTimeOffset? CancelledAtUtc { get; private set; }

    /// <summary>Gets why the dispatch was cancelled, recorded on the ledger.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>Gets the receipt identifier used as the arrival ledger reference.</summary>
    public TransferReceiptId? ReceiptId { get; private set; }

    /// <summary>Gets the human-readable TRC receipt number.</summary>
    public string? ReceiptNumber { get; private set; }

    /// <summary>Gets the user who receipted the stock, when received.</summary>
    public UserId? ReceivedByUserId { get; private set; }

    /// <summary>Gets the instant the stock arrived.</summary>
    public DateTimeOffset? ReceivedAtUtc { get; private set; }

    /// <summary>Gets the user who verified the transfer, when verified.</summary>
    public UserId? VerifiedByUserId { get; private set; }

    /// <summary>Gets the instant the transfer was verified and closed.</summary>
    public DateTimeOffset? VerifiedAtUtc { get; private set; }

    /// <summary>Gets the requested lines, in creation order.</summary>
    public IReadOnlyList<TransferLine> Lines => _lines;

    /// <summary>Gets the picking allocations, in pick order.</summary>
    public IReadOnlyList<TransferPickAllocation> Allocations => _allocations;

    /// <summary>Gets the arrival discrepancies, in arrival order.</summary>
    public IReadOnlyList<TransferDiscrepancy> Discrepancies => _discrepancies;

    /// <summary>Gets the custody timeline, in sequence order.</summary>
    public IReadOnlyList<TransferCustodyEvent> CustodyEvents => _custodyEvents;

    /// <summary>Gets the total estimated value of the request at approval time.</summary>
    /// <param name="unitCosts">The unit cost of each requested product, keyed by product identifier.</param>
    /// <returns>The estimated value of the requested quantities.</returns>
    public decimal EstimatedTotalValue(IReadOnlyDictionary<ProductId, decimal> unitCosts) =>
        _lines.Sum(l => unitCosts.TryGetValue(l.ProductId, out decimal unitCost) ? unitCost * l.RequestedQuantity : 0m);

    /// <summary>Gets the total value of the dispatch at the picked snapshot costs.</summary>
    public decimal TotalValue => _allocations.Sum(a => a.LineTotal);
}