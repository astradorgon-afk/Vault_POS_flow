using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Purchasing;

/// <summary>The lifecycle state of a supplier return.</summary>
public enum SupplierReturnStatus
{
    /// <summary>The return is being prepared and can still be changed.</summary>
    Draft = 1,

    /// <summary>The return has been submitted and awaits approval.</summary>
    PendingApproval = 2,

    /// <summary>An approver accepted the return; it can be dispatched.</summary>
    Approved = 3,

    /// <summary>The stock has left the building: the ledger group has been posted.</summary>
    Dispatched = 4,

    /// <summary>The supplier confirmed the return.</summary>
    Confirmed = 5,
}

/// <summary>Why a quantity is being returned to the supplier, per line.</summary>
public enum SupplierReturnReason
{
    /// <summary>The goods are defective or non-conforming.</summary>
    Defective = 1,

    /// <summary>The goods were damaged in transit or in store.</summary>
    Damaged = 2,

    /// <summary>The goods expired before they could be sold.</summary>
    Expired = 3,

    /// <summary>The goods are contaminated and unfit for sale.</summary>
    Contaminated = 4,

    /// <summary>The supplier delivered the wrong item.</summary>
    WrongItem = 5,

    /// <summary>Anything else. Requires explanatory notes on the line.</summary>
    Other = 99,
}

/// <summary>One line of a supplier return: a quantity of stock coming back.</summary>
/// <param name="ProductId">The product being returned.</param>
/// <param name="BatchId">The specific lot, when the product is batch-tracked.</param>
/// <param name="SourceState">The inventory state the stock is returned from; one
/// of <see cref="InventoryState.Damaged"/>, <see cref="InventoryState.Expired"/>
/// or <see cref="InventoryState.Quarantine"/>.</param>
/// <param name="Quantity">The quantity to return, in the product's base unit.</param>
/// <param name="UnitCost">The valuation cost per unit being returned.</param>
/// <param name="Reason">Why the goods are being returned.</param>
/// <param name="Notes">Required when <paramref name="Reason"/> is <see cref="SupplierReturnReason.Other"/>.</param>
/// <param name="TracksBatches">Whether the product is batch-tracked; the creating
/// handler fills this from the product master.</param>
public sealed record SupplierReturnLineSpec(
    ProductId ProductId,
    BatchId? BatchId,
    InventoryState SourceState,
    decimal Quantity,
    decimal UnitCost,
    SupplierReturnReason Reason,
    string? Notes = null,
    bool TracksBatches = false);

/// <summary>One line of a supplier return, as created.</summary>
public sealed class SupplierReturnLine : Entity<SupplierReturnLineId>
{
    internal SupplierReturnLine(
        SupplierReturnLineId id,
        SupplierReturnId supplierReturnId,
        int lineNo,
        ProductId productId,
        BatchId? batchId,
        InventoryState sourceState,
        decimal quantity,
        decimal unitCost,
        SupplierReturnReason reason,
        string? notes)
    {
        Id = id;
        SupplierReturnId = supplierReturnId;
        LineNo = lineNo;
        ProductId = productId;
        BatchId = batchId;
        SourceState = sourceState;
        Quantity = quantity;
        UnitCost = unitCost;
        Reason = reason;
        Notes = notes;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SupplierReturnLine()
    {
        ProductId = ProductId.Empty;
    }

    /// <summary>Gets the owning return.</summary>
    public SupplierReturnId SupplierReturnId { get; private set; }

    /// <summary>Gets the line number, for display.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the product being returned.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the specific lot being returned, when batch-tracked.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>Gets the inventory state the stock is returned from.</summary>
    public InventoryState SourceState { get; private set; }

    /// <summary>Gets the quantity to return, in the base unit.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the valuation cost per unit.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets why the goods are being returned.</summary>
    public SupplierReturnReason Reason { get; private set; }

    /// <summary>Gets the explanatory notes, required for <see cref="SupplierReturnReason.Other"/>.</summary>
    public string? Notes { get; private set; }

    /// <summary>Gets the line's value at the return cost.</summary>
    public decimal LineTotal => Quantity * UnitCost;
}

/// <summary>
/// A supplier return: a document under which stock that cannot be sold is sent
/// back to the supplier, posting <c>Loc/&lt;state&gt; −q → EXT-SUPPLIER +q</c>
/// against the <c>SRT-…</c> number when it is dispatched.
/// </summary>
/// <remarks>
/// <para>
/// A return may only source stock from <see cref="InventoryState.Damaged"/>,
/// <see cref="InventoryState.Expired"/> or <see cref="InventoryState.Quarantine"/>
/// — never from <see cref="InventoryState.Available"/>, so a return cannot be
/// used to quietly remove sellable stock. The ledger rule backs this up at post
/// time; the aggregate refuses such lines at creation.
/// </para>
/// <para>
/// The lifecycle is Draft → PendingApproval → Approved → Dispatched → Confirmed.
/// Dispatching requires an approval and the supplier's return-authorization
/// number, and posts the ledger group; the SRT number is allocated by the
/// handler just before posting so an invalid dispatch cannot burn a sequence.
/// </para>
/// </remarks>
public sealed class SupplierReturn : AggregateRoot<SupplierReturnId>
{
    private readonly List<SupplierReturnLine> _lines = [];

    private SupplierReturn(
        SupplierReturnId id,
        SupplierId supplierId,
        LocationId locationId,
        IReadOnlyList<SupplierReturnLine> lines,
        UserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        SupplierId = supplierId;
        LocationId = locationId;
        _lines.AddRange(lines);
        CreatedByUserId = createdBy;
        CreatedAtUtc = createdAtUtc;
        Status = SupplierReturnStatus.Draft;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SupplierReturn()
    {
        Number = string.Empty;
        SupplierId = SupplierId.Empty;
        LocationId = LocationId.Empty;
        CreatedByUserId = UserId.Empty;
    }

    /// <summary>Creates a draft return.</summary>
    /// <param name="supplierId">The supplier receiving the goods back.</param>
    /// <param name="locationId">The location the stock sits in.</param>
    /// <param name="specs">The lines to return.</param>
    /// <param name="createdBy">The user raising the return.</param>
    /// <param name="createdAtUtc">The current instant.</param>
    /// <returns>The draft return, or validation errors.</returns>
    public static Result<SupplierReturn> Create(
        SupplierId supplierId,
        LocationId locationId,
        IReadOnlyList<SupplierReturnLineSpec> specs,
        UserId createdBy,
        DateTimeOffset createdAtUtc)
    {
        if (specs is null || specs.Count == 0)
        {
            return Result<SupplierReturn>.Failure(PurchasingErrors.EmptyReturn);
        }

        List<Error> errors = [];
        List<SupplierReturnLine> lines = [];
        SupplierReturnId returnId = SupplierReturnId.New();

        for (int i = 0; i < specs.Count; i++)
        {
            SupplierReturnLineSpec spec = specs[i];
            int lineNo = i + 1;

            if (spec.Quantity <= 0m)
            {
                errors.Add(PurchasingErrors.ReturnQuantityInvalid(lineNo));
            }

            if (spec.UnitCost < 0m)
            {
                errors.Add(PurchasingErrors.ReturnUnitCostInvalid(lineNo));
            }

            if (spec.SourceState is not InventoryState.Damaged
                and not InventoryState.Expired
                and not InventoryState.Quarantine)
            {
                errors.Add(PurchasingErrors.ReturnSourceStateNotReturnable(lineNo, spec.SourceState));
            }

            if (spec.TracksBatches && (spec.BatchId is null || spec.BatchId.Value.IsEmpty))
            {
                errors.Add(PurchasingErrors.ReturnBatchRequired(lineNo));
            }

            if (!spec.TracksBatches && (spec.BatchId is { } providedBatch && !providedBatch.IsEmpty))
            {
                errors.Add(PurchasingErrors.ReturnBatchNotAllowed(lineNo));
            }

            if (spec.Reason == SupplierReturnReason.Other
                && string.IsNullOrWhiteSpace(spec.Notes))
            {
                errors.Add(PurchasingErrors.ReturnOtherReasonNotesRequired(lineNo));
            }

            bool duplicated = lines.Any(l =>
                l.ProductId == spec.ProductId
                && l.BatchId == spec.BatchId
                && l.SourceState == spec.SourceState);

            if (duplicated)
            {
                errors.Add(PurchasingErrors.DuplicateReturnLine(lineNo));
            }
            else
            {
                lines.Add(new SupplierReturnLine(
                    SupplierReturnLineId.New(),
                    returnId,
                    lineNo,
                    spec.ProductId,
                    spec.BatchId,
                    spec.SourceState,
                    spec.Quantity,
                    spec.UnitCost,
                    spec.Reason,
                    spec.Notes?.Trim()));
            }
        }

        return errors.Count == 0
            ? Result<SupplierReturn>.Success(new SupplierReturn(
                returnId,
                supplierId,
                locationId,
                lines,
                createdBy,
                createdAtUtc))
            : Result<SupplierReturn>.Failure(errors);
    }

    /// <summary>Submits the return for approval.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Submit(DateTimeOffset now)
    {
        if (Status != SupplierReturnStatus.Draft)
        {
            return Result.Failure(PurchasingErrors.InvalidReturnState(
                SupplierReturnStatus.Draft, Status));
        }

        Status = SupplierReturnStatus.PendingApproval;
        return Result.Success();
    }

    /// <summary>Approves the return so it can be dispatched.</summary>
    /// <param name="approver">The approving user.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Approve(UserId approver, DateTimeOffset now)
    {
        if (Status != SupplierReturnStatus.PendingApproval)
        {
            return Result.Failure(PurchasingErrors.InvalidReturnState(
                SupplierReturnStatus.PendingApproval, Status));
        }

        Status = SupplierReturnStatus.Approved;
        ApprovedByUserId = approver;
        ApprovedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Rejects the return, sending it back to draft for rework.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Reject(DateTimeOffset now)
    {
        if (Status != SupplierReturnStatus.PendingApproval)
        {
            return Result.Failure(PurchasingErrors.InvalidReturnState(
                SupplierReturnStatus.PendingApproval, Status));
        }

        Status = SupplierReturnStatus.Draft;
        return Result.Success();
    }

    /// <summary>Records the human-readable SRT number.</summary>
    /// <param name="number">The allocated number.</param>
    /// <returns>Success, or a conflict when already numbered.</returns>
    public Result AssignNumber(DocumentNumber number)
    {
        if (Status != SupplierReturnStatus.Approved)
        {
            return Result.Failure(PurchasingErrors.InvalidReturnState(
                SupplierReturnStatus.Approved, Status));
        }

        if (Number.Length > 0)
        {
            return Result.Failure(PurchasingErrors.ReturnAlreadyNumbered(Id));
        }

        Number = number.Value;
        return Result.Success();
    }

    /// <summary>
    /// Dispatches the return: the stock leaves the location and the ledger group
    /// is posted against the SRT number.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="supplierAuthorizationNumber">The return-authorization number
    /// the supplier gave for the return.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Dispatch(DateTimeOffset now, string? supplierAuthorizationNumber)
    {
        if (Status != SupplierReturnStatus.Approved)
        {
            return Result.Failure(PurchasingErrors.InvalidReturnState(
                SupplierReturnStatus.Approved, Status));
        }

        if (string.IsNullOrWhiteSpace(supplierAuthorizationNumber))
        {
            return Result.Failure(PurchasingErrors.ReturnSupplierAuthorizationRequired);
        }

        Status = SupplierReturnStatus.Dispatched;
        SupplierAuthorizationNumber = supplierAuthorizationNumber.Trim();
        DispatchedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Confirms the supplier accepted the return.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Confirm(DateTimeOffset now)
    {
        if (Status != SupplierReturnStatus.Dispatched)
        {
            return Result.Failure(PurchasingErrors.InvalidReturnState(
                SupplierReturnStatus.Dispatched, Status));
        }

        Status = SupplierReturnStatus.Confirmed;
        ConfirmedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Gets the lifecycle status.</summary>
    public SupplierReturnStatus Status { get; private set; }

    /// <summary>Gets the SRT document number, allocated at dispatch.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>Gets the supplier receiving the goods back.</summary>
    public SupplierId SupplierId { get; private set; }

    /// <summary>Gets the location the stock sits in.</summary>
    public LocationId LocationId { get; private set; }

    /// <summary>Gets the user who raised the return.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets the instant the return was raised.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the user who approved the return, when approved.</summary>
    public UserId? ApprovedByUserId { get; private set; }

    /// <summary>Gets the instant the return was approved, when approved.</summary>
    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    /// <summary>Gets the supplier's return-authorization number, recorded at dispatch.</summary>
    public string? SupplierAuthorizationNumber { get; private set; }

    /// <summary>Gets the instant the return was dispatched, when dispatched.</summary>
    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    /// <summary>Gets the instant the supplier confirmed the return, when confirmed.</summary>
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    /// <summary>Gets the return lines, in creation order.</summary>
    public IReadOnlyList<SupplierReturnLine> Lines => _lines;

    /// <summary>Gets the total value of the return at the return costs.</summary>
    public decimal TotalValue => _lines.Sum(l => l.LineTotal);
}