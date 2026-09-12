using Pos.Domain.Common;

namespace Pos.Domain.Purchasing;

/// <summary>Purchase order lifecycle status. Persisted as <see cref="short"/>; values must never be renumbered.</summary>
public enum PurchaseOrderStatus
{
    /// <summary>A draft purchase order awaiting approval.</summary>
    Draft = 0,

    /// <summary>Submitted and awaiting an approval decision.</summary>
    PendingApproval = 1,

    /// <summary>Approved by a user with sufficient tier authority.</summary>
    Approved = 2,

    /// <summary>The approval was denied. The order is frozen; re-draft copies to a new PO.</summary>
    Rejected = 3,

    /// <summary>Approved and sent to the supplier.</summary>
    Ordered = 4,

    /// <summary>One or more goods receipts have been recorded.</summary>
    PartiallyReceived = 5,

    /// <summary>All ordered goods have been received.</summary>
    FullyReceived = 6,

    /// <summary>Approved and either fully received or received with a closing decision.</summary>
    Closed = 7,

    /// <summary>The order was cancelled before or after commitment.</summary>
    Cancelled = 8,
}

/// <summary>The decision recorded on a purchase order approval row. Persisted as <see cref="short"/>.</summary>
public enum PurchaseApprovalDecision
{
    /// <summary>The order was approved.</summary>
    Approve = 1,

    /// <summary>The approval was denied.</summary>
    Reject = 2,

    /// <summary>The order was cancelled.</summary>
    Cancel = 3,

    /// <summary>The order was closed.</summary>
    Close = 4,
}

/// <summary>Input to create a purchase order line.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="UnitOfMeasureId">The unit of measure to buy in.</param>
/// <param name="OrderedQuantity">The quantity to order, in the storage unit.</param>
/// <param name="UnitCost">The purchase cost per base unit.</param>
public sealed record PurchaseOrderLineSpec(
    ProductId ProductId,
    UnitOfMeasureId UnitOfMeasureId,
    decimal OrderedQuantity,
    decimal UnitCost);

/// <summary>A single line on a purchase order.</summary>
public sealed class PurchaseOrderLine : Entity<PurchaseOrderLineId>
{
    /// <summary>Initializes a new <see cref="PurchaseOrderLine"/>.</summary>
    private PurchaseOrderLine(
        PurchaseOrderLineId id,
        PurchaseOrderId purchaseOrderId,
        int lineNo,
        ProductId productId,
        UnitOfMeasureId unitOfMeasureId,
        decimal orderedQuantity,
        decimal unitCost)
    {
        Id = id;
        PurchaseOrderId = purchaseOrderId;
        LineNo = lineNo;
        ProductId = productId;
        UnitOfMeasureId = unitOfMeasureId;
        OrderedQuantity = orderedQuantity;
        UnitCost = unitCost;
        LineTotal = Math.Round(orderedQuantity * unitCost, Money.StorageScale, MidpointRounding.AwayFromZero);
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private PurchaseOrderLine()
    {
        PurchaseOrderId = PurchaseOrderId.Empty;
        ProductId = ProductId.Empty;
        UnitOfMeasureId = UnitOfMeasureId.Empty;
    }

    /// <summary>Gets the parent purchase order identifier.</summary>
    public PurchaseOrderId PurchaseOrderId { get; private set; }

    /// <summary>Gets the sequential line number within the purchase order.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the ordered product.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>The unit of measure the product is bought in.</summary>
    public UnitOfMeasureId UnitOfMeasureId { get; private set; }

    /// <summary>Gets the quantity ordered, in the base unit of the product.</summary>
    public decimal OrderedQuantity { get; private set; }

    /// <summary>Gets the cost per base unit.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets the computed line total: ordered quantity multiplied by unit cost.</summary>
    public decimal LineTotal { get; private set; }

    /// <summary>
    /// Creates a line within an existing purchase order. The order reference and
    /// line number are supplied by the parent aggregate.
    /// </summary>
    internal static PurchaseOrderLine Create(
        PurchaseOrderId purchaseOrderId,
        int lineNo,
        ProductId productId,
        UnitOfMeasureId unitOfMeasureId,
        decimal orderedQuantity,
        decimal unitCost)
        => new(
            PurchaseOrderLineId.New(),
            purchaseOrderId,
            lineNo,
            productId,
            unitOfMeasureId,
            Math.Round(orderedQuantity, Quantity.Scale, MidpointRounding.AwayFromZero),
            Math.Round(unitCost, Money.StorageScale, MidpointRounding.AwayFromZero));
}

/// <summary>A decision recorded against a purchase order by an authorised approver.</summary>
public sealed class PurchaseApproval : Entity<PurchaseApprovalId>
{
    private PurchaseApproval(
        PurchaseApprovalId id,
        PurchaseOrderId purchaseOrderId,
        UserId approverUserId,
        PurchaseApprovalDecision decision,
        DateTimeOffset decidedAtUtc,
        string? notes,
        decimal thresholdApplied)
    {
        Id = id;
        PurchaseOrderId = purchaseOrderId;
        ApproverUserId = approverUserId;
        Decision = decision;
        DecidedAtUtc = decidedAtUtc;
        Notes = notes;
        ThresholdApplied = thresholdApplied;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private PurchaseApproval()
    {
        PurchaseOrderId = PurchaseOrderId.Empty;
        ApproverUserId = UserId.Empty;
    }

    /// <summary>Gets the parent purchase order identifier.</summary>
    public PurchaseOrderId PurchaseOrderId { get; private set; }

    /// <summary>Gets the user who recorded the decision.</summary>
    public UserId ApproverUserId { get; private set; }

    /// <summary>Gets the decision.</summary>
    public PurchaseApprovalDecision Decision { get; private set; }

    /// <summary>Gets the UTC date and time the decision was recorded.</summary>
    public DateTimeOffset DecidedAtUtc { get; private set; }

    /// <summary>Gets optional notes attached to the decision.</summary>
    public string? Notes { get; private set; }

    /// <summary>
    /// The monetary threshold applied to this decision: the PO grand total at the
    /// time of approval, for audit traceability.
    /// </summary>
    public decimal ThresholdApplied { get; private set; }

    /// <summary>Records a decision. Only invoked by the parent aggregate.</summary>
    internal static PurchaseApproval Create(
        PurchaseOrderId purchaseOrderId,
        UserId approver,
        PurchaseApprovalDecision decision,
        DateTimeOffset now,
        decimal thresholdApplied,
        string? notes)
        => new(
            PurchaseApprovalId.New(),
            purchaseOrderId,
            approver,
            decision,
            now,
            notes?.Trim(),
            thresholdApplied);
}

/// <summary>
/// A purchase order: the commitment to buy goods from a supplier against an
/// agreed cost, tracked through its lifecycle from draft to delivery.
/// </summary>
public sealed class PurchaseOrder : AggregateRoot<PurchaseOrderId>
{
    private readonly List<PurchaseOrderLine> _lines = [];
    private readonly List<PurchaseApproval> _approvals = [];

    private PurchaseOrder(
        PurchaseOrderId id,
        SupplierId supplierId,
        LocationId destinationLocationId,
        decimal subtotal,
        UserId createdBy,
        DateTimeOffset createdAtUtc,
        string currencyCode,
        DateTimeOffset? expectedAtUtc)
    {
        Id = id;
        SupplierId = supplierId;
        DestinationLocationId = destinationLocationId;
        Subtotal = subtotal;
        TaxTotal = 0m;
        GrandTotal = subtotal;
        CreatedByUserId = createdBy;
        CreatedAtUtc = createdAtUtc;
        CurrencyCode = currencyCode;
        ExpectedAtUtc = expectedAtUtc;
        Status = PurchaseOrderStatus.Draft;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private PurchaseOrder()
    {
        SupplierId = SupplierId.Empty;
        DestinationLocationId = LocationId.Empty;
        CreatedByUserId = UserId.Empty;
        CurrencyCode = string.Empty;
    }

    /// <summary>Gets the document number, or <see langword="null"/> until the order is submitted.</summary>
    public string? Number { get; private set; }

    /// <summary>Gets the lifecycle status.</summary>
    public PurchaseOrderStatus Status { get; private set; }

    /// <summary>Gets the supplier the order is placed with.</summary>
    public SupplierId SupplierId { get; private set; }

    /// <summary>Gets the location that will receive the goods.</summary>
    public LocationId DestinationLocationId { get; private set; }

    /// <summary>Gets the three-letter ISO 4217 currency code.</summary>
    public string CurrencyCode { get; private set; }

    /// <summary>Gets the total value of ordered goods, excluding tax.</summary>
    public decimal Subtotal { get; private set; }

    /// <summary>
    /// Gets the tax total. Defaults to zero until a tax engine is integrated;
    /// tax information is carried on each line for forward compatibility.
    /// </summary>
    public decimal TaxTotal { get; private set; }

    /// <summary>Gets the total value of the order including tax.</summary>
    public decimal GrandTotal { get; private set; }

    /// <summary>Gets the date and time the order was sent to the supplier, or <see langword="null"/>.</summary>
    public DateTimeOffset? OrderedAtUtc { get; private set; }

    /// <summary>Gets the expected delivery date, or <see langword="null"/>.</summary>
    public DateTimeOffset? ExpectedAtUtc { get; private set; }

    /// <summary>Gets the user who created the order.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets the date and time the order was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the reason the order was cancelled, or <see langword="null"/>.</summary>
    public string? CancelledReason { get; private set; }

    /// <summary>Gets the reason the order was closed, or <see langword="null"/>.</summary>
    public string? ClosedReason { get; private set; }

    /// <summary>Gets the order lines.</summary>
    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    /// <summary>Gets the approval decisions recorded against this order.</summary>
    public IReadOnlyList<PurchaseApproval> Approvals => _approvals;

    /// <summary>
    /// Creates a new draft purchase order. The order is assigned no document number
    /// until <see cref="Submit"/> is called.
    /// </summary>
    public static Result<PurchaseOrder> Create(
        SupplierId supplierId,
        LocationId destinationLocationId,
        IReadOnlyList<PurchaseOrderLineSpec> specs,
        UserId createdBy,
        DateTimeOffset now,
        string? currencyCode,
        DateTimeOffset? expectedAtUtc)
    {
        if (supplierId.IsEmpty)
        {
            return Result<PurchaseOrder>.Failure(new Error(
                "purchasing.supplier_invalid",
                "A supplier must be specified on a purchase order."));
        }

        if (destinationLocationId.IsEmpty)
        {
            return Result<PurchaseOrder>.Failure(new Error(
                "purchasing.destination_invalid",
                "A destination location must be specified on a purchase order."));
        }

        if (specs is null || specs.Count == 0)
        {
            return Result<PurchaseOrder>.Failure(PurchasingErrors.EmptyOrder);
        }

        PurchaseOrderId orderId = PurchaseOrderId.New();

        List<PurchaseOrderLine> lines = [];
        HashSet<Guid> seenProducts = [];

        for (int i = 0; i < specs.Count; i++)
        {
            PurchaseOrderLineSpec spec = specs[i];
            int lineNo = i + 1;

            if (spec.OrderedQuantity <= 0m)
            {
                return Result<PurchaseOrder>.Failure(PurchasingErrors.InvalidQuantity(lineNo));
            }

            if (spec.UnitCost < 0m)
            {
                return Result<PurchaseOrder>.Failure(PurchasingErrors.InvalidUnitCost(lineNo));
            }

            if (!seenProducts.Add(spec.ProductId.Value))
            {
                return Result<PurchaseOrder>.Failure(PurchasingErrors.DuplicateProductLine(spec.ProductId));
            }

            PurchaseOrderLine line = PurchaseOrderLine.Create(
                orderId,
                lineNo,
                spec.ProductId,
                spec.UnitOfMeasureId,
                spec.OrderedQuantity,
                spec.UnitCost);

            lines.Add(line);
        }

        decimal grandTotal = lines.Sum(l => l.LineTotal);

        PurchaseOrder order = new(
            orderId,
            supplierId,
            destinationLocationId,
            grandTotal,
            createdBy,
            now,
            currencyCode ?? "PHP",
            expectedAtUtc);

        foreach (PurchaseOrderLine line in lines)
        {
            order._lines.Add(line);
        }

        return Result<PurchaseOrder>.Success(order);
    }

    /// <summary>
    /// Submits the order for approval. A document number is supplied by the
    /// handler (allocated from the central counter) at submission time so that
    /// cancelled drafts never burn a number.
    /// </summary>
    /// <param name="number">The allocated document number.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a validation error.</returns>
    public Result Submit(DocumentNumber number, DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            return Result.Failure(PurchasingErrors.InvalidState(PurchaseOrderStatus.Draft, Status));
        }

        Number = number.Value;
        Status = PurchaseOrderStatus.PendingApproval;
        return Result.Success();
    }

    /// <summary>Transitions from PendingApproval to Approved and records the decision.</summary>
    public Result Approve(UserId approver, DateTimeOffset now, decimal thresholdApplied, string? notes = null)
    {
        if (Status != PurchaseOrderStatus.PendingApproval)
        {
            return Result.Failure(PurchasingErrors.InvalidState(PurchaseOrderStatus.PendingApproval, Status));
        }

        _approvals.Add(PurchaseApproval.Create(Id, approver, PurchaseApprovalDecision.Approve, now, thresholdApplied, notes));
        Status = PurchaseOrderStatus.Approved;
        return Result.Success();
    }

    /// <summary>Transitions from PendingApproval to Rejected and records the decision.</summary>
    public Result Reject(UserId approver, DateTimeOffset now, string? notes = null)
    {
        if (Status != PurchaseOrderStatus.PendingApproval)
        {
            return Result.Failure(PurchasingErrors.InvalidState(PurchaseOrderStatus.PendingApproval, Status));
        }

        _approvals.Add(PurchaseApproval.Create(Id, approver, PurchaseApprovalDecision.Reject, now, GrandTotal, notes));
        Status = PurchaseOrderStatus.Rejected;
        return Result.Success();
    }

    /// <summary>Transitions from Approved to Ordered.</summary>
    public Result Send(DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Approved)
        {
            return Result.Failure(PurchasingErrors.InvalidState(PurchaseOrderStatus.Approved, Status));
        }

        OrderedAtUtc = now;
        Status = PurchaseOrderStatus.Ordered;
        return Result.Success();
    }

    /// <summary>
    /// Cancels an order that has not yet been fully committed. Draft orders may
    /// be withdrawn without a reason; submitted orders always need one.
    /// </summary>
    public Result Cancel(UserId actor, string? reason, DateTimeOffset now)
    {
        PurchaseOrderStatus[] allowed =
        [
            PurchaseOrderStatus.Draft,
            PurchaseOrderStatus.PendingApproval,
            PurchaseOrderStatus.Approved,
            PurchaseOrderStatus.Ordered,
        ];

        if (!allowed.Contains(Status))
        {
            return Result.Failure(PurchasingErrors.InvalidState(allowed, Status));
        }

        if (Status != PurchaseOrderStatus.Draft && string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(PurchasingErrors.CancelReasonRequired);
        }

        _approvals.Add(PurchaseApproval.Create(Id, actor, PurchaseApprovalDecision.Cancel, now, GrandTotal, reason));
        CancelledReason = reason?.Trim();
        Status = PurchaseOrderStatus.Cancelled;
        return Result.Success();
    }

    /// <summary>
    /// Withdraws a draft. The order must still be in Draft status and must not
    /// have been allocated a document number.
    /// </summary>
    public Result Withdraw(UserId actor, DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            return Result.Failure(PurchasingErrors.InvalidState(PurchaseOrderStatus.Draft, Status));
        }

        if (Number is not null)
        {
            return Result.Failure(PurchasingErrors.NumberedCannotBeWithdrawn);
        }

        _approvals.Add(PurchaseApproval.Create(Id, actor, PurchaseApprovalDecision.Cancel, now, GrandTotal, null));
        Status = PurchaseOrderStatus.Cancelled;
        return Result.Success();
    }

    /// <summary>
    /// Closes an order. Fully received orders may be closed without a reason;
    /// partially received orders require one.
    /// </summary>
    public Result Close(UserId actor, string? reason, DateTimeOffset now)
    {
        PurchaseOrderStatus[] allowed =
        [
            PurchaseOrderStatus.Ordered,
            PurchaseOrderStatus.PartiallyReceived,
            PurchaseOrderStatus.FullyReceived,
        ];

        if (!allowed.Contains(Status))
        {
            return Result.Failure(PurchasingErrors.InvalidState(allowed, Status));
        }

        if (Status != PurchaseOrderStatus.FullyReceived && string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(PurchasingErrors.CloseReasonRequired);
        }

        _approvals.Add(PurchaseApproval.Create(Id, actor, PurchaseApprovalDecision.Close, now, GrandTotal, reason));
        ClosedReason = reason?.Trim();
        Status = PurchaseOrderStatus.Closed;
        return Result.Success();
    }

    /// <summary>
    /// Records goods received against this order. Invoked by the receiving
    /// handler in Part 2 to transition Ordered/PartiallyReceived to
    /// PartiallyReceived or FullyReceived.
    /// </summary>
    /// <param name="cumulativeReceivedByLine">
    /// Total quantity received to date, keyed by line identifier.
    /// </param>
    /// <returns>Success, or a state error.</returns>
    internal Result RecordReceipt(IReadOnlyDictionary<PurchaseOrderLineId, decimal> cumulativeReceivedByLine)
    {
        if (Status is not PurchaseOrderStatus.Ordered and not PurchaseOrderStatus.PartiallyReceived)
        {
            return Result.Failure(PurchasingErrors.InvalidState(
                new PurchaseOrderStatus[]
                {
                    PurchaseOrderStatus.Ordered,
                    PurchaseOrderStatus.PartiallyReceived,
                }, Status));
        }

        bool allFullyReceived = _lines.All(line =>
            cumulativeReceivedByLine.TryGetValue(line.Id, out decimal received)
            && received >= line.OrderedQuantity);

        Status = allFullyReceived
            ? PurchaseOrderStatus.FullyReceived
            : PurchaseOrderStatus.PartiallyReceived;

        return Result.Success();
    }
}