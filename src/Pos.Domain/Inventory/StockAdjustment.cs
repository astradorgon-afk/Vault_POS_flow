using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>The lifecycle state of a stock adjustment.</summary>
public enum StockAdjustmentStatus
{
    /// <summary>Being prepared; not yet visible to approvers.</summary>
    Draft = 1,

    /// <summary>Submitted and waiting for someone else to approve it.</summary>
    PendingApproval = 2,

    /// <summary>Refused by an approver. Terminal; nothing was posted.</summary>
    Rejected = 3,

    /// <summary>Approved and posted to the ledger under its ADJ number.</summary>
    Posted = 4,

    /// <summary>Posted, then undone by reversal movements. Terminal.</summary>
    Reversed = 5,
}

/// <summary>One requested change to a bucket.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch, for batch-tracked products.</param>
/// <param name="State">The bucket's inventory state.</param>
/// <param name="QuantityDelta">The signed change in base units; negative removes stock.</param>
/// <param name="UnitCost">The valuation cost per unit, resolved by the handler from the bucket.</param>
/// <param name="TracksBatches">Whether the product is batch-tracked, from the product master.</param>
public sealed record StockAdjustmentLineSpec(
    ProductId ProductId,
    BatchId? BatchId,
    InventoryState State,
    decimal QuantityDelta,
    decimal UnitCost,
    bool TracksBatches = false);

/// <summary>One line of a stock adjustment.</summary>
public sealed class StockAdjustmentLine : Entity<StockAdjustmentLineId>
{
    internal StockAdjustmentLine(
        StockAdjustmentLineId id,
        StockAdjustmentId adjustmentId,
        int lineNo,
        StockAdjustmentLineSpec spec,
        InventoryMovementType movementType)
    {
        Id = id;
        StockAdjustmentId = adjustmentId;
        LineNo = lineNo;
        ProductId = spec.ProductId;
        BatchId = spec.BatchId is { IsEmpty: false } batch ? batch : null;
        State = spec.State;
        QuantityDelta = spec.QuantityDelta;
        UnitCost = spec.UnitCost;
        MovementType = movementType;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private StockAdjustmentLine()
    {
    }

    /// <summary>Gets the owning adjustment.</summary>
    public StockAdjustmentId StockAdjustmentId { get; private set; }

    /// <summary>Gets the line number.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the product.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the batch, for batch-tracked products.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>Gets the bucket's inventory state.</summary>
    public InventoryState State { get; private set; }

    /// <summary>Gets the signed change in base units.</summary>
    public decimal QuantityDelta { get; private set; }

    /// <summary>Gets the valuation cost per unit captured when the adjustment was raised.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets the ledger movement type this line posts as.</summary>
    public InventoryMovementType MovementType { get; private set; }

    /// <summary>Gets the absolute value of the change, which approval tiers are measured against.</summary>
    public decimal AbsoluteValue => Math.Abs(QuantityDelta * UnitCost);
}

/// <summary>
/// A request to change stock outside the normal document flows — damage,
/// spoilage, loss, theft, expiry or a correction — posted to the ledger only
/// once someone other than its author approves it.
/// </summary>
/// <remarks>
/// <para>
/// The reason decides what the adjustment may do. Write-off reasons only remove
/// stock, to the EXT-WRITEOFF counterparty; <see cref="AdjustmentReasonCode.Expired"/>
/// moves available stock to the Expired state or writes off stock already there;
/// only <see cref="AdjustmentReasonCode.Other"/>, with notes, may add stock. Counts
/// correct stock through <see cref="InventoryCount"/>, never through an adjustment.
/// </para>
/// <para>
/// The unit cost of every line is captured when the adjustment is raised, so the
/// value an approver's tier is measured against cannot move while it waits.
/// </para>
/// </remarks>
public sealed class StockAdjustment : AggregateRoot<StockAdjustmentId>
{
    /// <summary>The shortest note accepted with the reason <c>Other</c>.</summary>
    public const int MinimumOtherNotesLength = 10;

    /// <summary>The longest note accepted.</summary>
    public const int NotesMaxLength = 512;

    private static readonly HashSet<InventoryState> WriteOffStates =
    [
        InventoryState.Available, InventoryState.Damaged, InventoryState.Quarantine,
        InventoryState.PendingInspection, InventoryState.ReturnPending, InventoryState.Expired,
    ];

    private static readonly HashSet<InventoryState> CorrectionStates =
    [
        InventoryState.Available, InventoryState.Damaged, InventoryState.Expired, InventoryState.Quarantine,
    ];

    private readonly List<StockAdjustmentLine> _lines = [];

    private StockAdjustment(
        StockAdjustmentId id,
        LocationId locationId,
        AdjustmentReasonCode reason,
        string? notes,
        IEnumerable<StockAdjustmentLine> lines,
        UserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        LocationId = locationId;
        Reason = reason;
        Notes = notes;
        _lines.AddRange(lines);
        CreatedByUserId = createdBy;
        CreatedAtUtc = createdAtUtc;
        Status = StockAdjustmentStatus.Draft;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private StockAdjustment()
    {
    }

    /// <summary>Gets the ADJ number, allocated when the adjustment is posted.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>Gets the location whose stock changes.</summary>
    public LocationId LocationId { get; private set; }

    /// <summary>Gets why the stock changes.</summary>
    public AdjustmentReasonCode Reason { get; private set; }

    /// <summary>Gets the author's notes.</summary>
    public string? Notes { get; private set; }

    /// <summary>Gets the lifecycle state.</summary>
    public StockAdjustmentStatus Status { get; private set; }

    /// <summary>Gets who raised the adjustment.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets when it was raised.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets when it was submitted for approval.</summary>
    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    /// <summary>Gets who approved or rejected it.</summary>
    public UserId? DecidedByUserId { get; private set; }

    /// <summary>Gets when it was approved or rejected.</summary>
    public DateTimeOffset? DecidedAtUtc { get; private set; }

    /// <summary>Gets why it was rejected.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>Gets who reversed it.</summary>
    public UserId? ReversedByUserId { get; private set; }

    /// <summary>Gets when it was reversed.</summary>
    public DateTimeOffset? ReversedAtUtc { get; private set; }

    /// <summary>Gets why it was reversed.</summary>
    public string? ReversalReason { get; private set; }

    /// <summary>Gets the lines, in order.</summary>
    public IReadOnlyList<StockAdjustmentLine> Lines => _lines;

    /// <summary>Gets the total absolute value, which approval tiers are measured against.</summary>
    public decimal TotalAbsoluteValue => _lines.Sum(l => l.AbsoluteValue);

    /// <summary>Raises a draft adjustment.</summary>
    /// <param name="locationId">The location whose stock changes.</param>
    /// <param name="reason">Why the stock changes.</param>
    /// <param name="notes">Notes; required for <see cref="AdjustmentReasonCode.Other"/>.</param>
    /// <param name="specs">The lines.</param>
    /// <param name="createdBy">Who raises it.</param>
    /// <param name="createdAtUtc">The current instant.</param>
    /// <returns>The draft, or every validation failure found.</returns>
    public static Result<StockAdjustment> Create(
        LocationId locationId,
        AdjustmentReasonCode reason,
        string? notes,
        IReadOnlyList<StockAdjustmentLineSpec> specs,
        UserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        if (specs is null || specs.Count == 0)
        {
            return Result<StockAdjustment>.Failure(InventoryControlErrors.AdjustmentEmpty);
        }

        if (reason is AdjustmentReasonCode.CountCorrection or AdjustmentReasonCode.SupplierReturn
            or AdjustmentReasonCode.TransferCancelled or AdjustmentReasonCode.TransitVarianceFound
            or AdjustmentReasonCode.TransitVarianceWriteOff or AdjustmentReasonCode.EmergencyTransfer
            or AdjustmentReasonCode.EmergencyTransferReversed || !Enum.IsDefined(reason))
        {
            return Result<StockAdjustment>.Failure(InventoryControlErrors.AdjustmentReasonNotAllowed(reason));
        }

        string? trimmedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        if (reason == AdjustmentReasonCode.Other && (trimmedNotes?.Length ?? 0) < MinimumOtherNotesLength)
        {
            return Result<StockAdjustment>.Failure(InventoryControlErrors.AdjustmentNotesRequired);
        }

        StockAdjustmentId id = StockAdjustmentId.New();
        List<Error> errors = [];
        List<StockAdjustmentLine> lines = [];

        for (int i = 0; i < specs.Count; i++)
        {
            StockAdjustmentLineSpec spec = specs[i];
            int lineNo = i + 1;
            int errorsBefore = errors.Count;

            ValidateLine(spec, lineNo, errors);
            InventoryMovementType? type = MovementTypeFor(reason, spec, lineNo, errors);

            if (lines.Any(l => l.ProductId == spec.ProductId
                               && l.BatchId == (spec.BatchId is { IsEmpty: false } b ? b : null)
                               && l.State == spec.State))
            {
                errors.Add(InventoryControlErrors.DuplicateLine(lineNo));
            }

            if (errors.Count == errorsBefore && type is { } movementType)
            {
                lines.Add(new StockAdjustmentLine(StockAdjustmentLineId.New(), id, lineNo, spec, movementType));
            }
        }

        return errors.Count > 0
            ? Result<StockAdjustment>.Failure(errors)
            : Result<StockAdjustment>.Success(
                new StockAdjustment(id, locationId, reason, trimmedNotes, lines, createdBy, createdAtUtc));
    }

    /// <summary>Submits the draft for approval.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Submit(DateTimeOffset now)
    {
        if (Status != StockAdjustmentStatus.Draft)
        {
            return Result.Failure(InventoryControlErrors.AdjustmentInvalidState(StockAdjustmentStatus.Draft, Status));
        }

        Status = StockAdjustmentStatus.PendingApproval;
        SubmittedAtUtc = now;
        return Result.Success();
    }

    /// <summary>
    /// Approves and posts the adjustment. The caller has already checked the
    /// approver's tier and posted the ledger movements in the same transaction.
    /// </summary>
    /// <param name="number">The ADJ number the movements were posted under.</param>
    /// <param name="approver">Who approved it.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Post(DocumentNumber number, UserId approver, DateTimeOffset now)
    {
        if (Status != StockAdjustmentStatus.PendingApproval)
        {
            return Result.Failure(InventoryControlErrors.AdjustmentInvalidState(StockAdjustmentStatus.PendingApproval, Status));
        }

        Number = number.Value;
        Status = StockAdjustmentStatus.Posted;
        DecidedByUserId = approver;
        DecidedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Refuses the adjustment. Nothing is posted.</summary>
    /// <param name="rejector">Who refused it.</param>
    /// <param name="reason">Why.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Reject(UserId rejector, string? reason, DateTimeOffset now)
    {
        if (Status != StockAdjustmentStatus.PendingApproval)
        {
            return Result.Failure(InventoryControlErrors.AdjustmentInvalidState(StockAdjustmentStatus.PendingApproval, Status));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            return Result.Failure(InventoryControlErrors.ReasonRequired);
        }

        Status = StockAdjustmentStatus.Rejected;
        DecidedByUserId = rejector;
        DecidedAtUtc = now;
        RejectionReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>
    /// Marks a posted adjustment reversed. The caller posts the reversal movements
    /// in the same transaction.
    /// </summary>
    /// <param name="reverser">Who reversed it.</param>
    /// <param name="reason">Why.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Reverse(UserId reverser, string? reason, DateTimeOffset now)
    {
        if (Status != StockAdjustmentStatus.Posted)
        {
            return Result.Failure(InventoryControlErrors.AdjustmentInvalidState(StockAdjustmentStatus.Posted, Status));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            return Result.Failure(InventoryControlErrors.ReasonRequired);
        }

        Status = StockAdjustmentStatus.Reversed;
        ReversedByUserId = reverser;
        ReversedAtUtc = now;
        ReversalReason = reason.Trim();
        return Result.Success();
    }

    private static void ValidateLine(StockAdjustmentLineSpec spec, int lineNo, List<Error> errors)
    {
        if (spec.QuantityDelta == 0m)
        {
            errors.Add(InventoryControlErrors.AdjustmentQuantityZero(lineNo));
        }

        if (spec.UnitCost < 0m)
        {
            errors.Add(InventoryControlErrors.UnitCostNegative(lineNo));
        }

        bool hasBatch = spec.BatchId is { IsEmpty: false };

        if (spec.TracksBatches && !hasBatch)
        {
            errors.Add(InventoryControlErrors.BatchRequired(lineNo));
        }
        else if (!spec.TracksBatches && hasBatch)
        {
            errors.Add(InventoryControlErrors.BatchNotAllowed(lineNo));
        }
    }

    private static InventoryMovementType? MovementTypeFor(
        AdjustmentReasonCode reason,
        StockAdjustmentLineSpec spec,
        int lineNo,
        List<Error> errors)
    {
        if (reason == AdjustmentReasonCode.Other)
        {
            if (CorrectionStates.Contains(spec.State))
            {
                return InventoryMovementType.ApprovedStockAdjustment;
            }

            errors.Add(InventoryControlErrors.AdjustmentStateNotAllowed(lineNo, spec.State));
            return null;
        }

        if (spec.QuantityDelta > 0m)
        {
            errors.Add(InventoryControlErrors.AdjustmentMustRemoveStock(lineNo));
            return null;
        }

        InventoryMovementType? type = reason switch
        {
            AdjustmentReasonCode.Expired when spec.State == InventoryState.Available => InventoryMovementType.ExpiryQuarantine,
            AdjustmentReasonCode.Expired when spec.State == InventoryState.Expired => InventoryMovementType.ExpiryWriteOff,
            AdjustmentReasonCode.Expired => null,
            _ when !WriteOffStates.Contains(spec.State) => null,
            AdjustmentReasonCode.Spoilage => InventoryMovementType.Spoilage,
            AdjustmentReasonCode.Loss => InventoryMovementType.Loss,
            AdjustmentReasonCode.Theft => InventoryMovementType.Theft,
            _ => InventoryMovementType.Damage,
        };

        if (type is null)
        {
            errors.Add(InventoryControlErrors.AdjustmentStateNotAllowed(lineNo, spec.State));
        }

        return type;
    }
}

/// <summary>Identifies a stock adjustment line.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct StockAdjustmentLineId(Guid Value) : IStronglyTypedId, IComparable<StockAdjustmentLineId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static StockAdjustmentLineId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="StockAdjustmentLineId"/>.</returns>
    public static StockAdjustmentLineId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(StockAdjustmentLineId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
