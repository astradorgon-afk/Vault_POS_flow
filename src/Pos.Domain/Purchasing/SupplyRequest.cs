using Pos.Domain.Common;

namespace Pos.Domain.Purchasing;

/// <summary>The lifecycle state of a supply request.</summary>
public enum SupplyRequestStatus
{
    /// <summary>Raised by the warehouse and waiting for the owner's decision.</summary>
    Submitted = 1,

    /// <summary>Some of the request has been placed on a purchase order; the rest has not.</summary>
    PartiallyOrdered = 2,

    /// <summary>Every line has been placed on a purchase order. Terminal.</summary>
    Ordered = 3,

    /// <summary>The owner turned the request down. Terminal.</summary>
    Rejected = 4,

    /// <summary>Withdrawn by the warehouse before a decision. Terminal.</summary>
    Cancelled = 5,
}

/// <summary>One product the warehouse is asking for.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Quantity">How much is wanted, in the product's base unit.</param>
/// <param name="Note">Why, or anything the owner should know when buying it.</param>
public sealed record SupplyRequestLineSpec(ProductId ProductId, decimal Quantity, string? Note = null);

/// <summary>One line of a supply request, as raised.</summary>
public sealed class SupplyRequestLine : Entity<SupplyRequestLineId>
{
    internal SupplyRequestLine(
        SupplyRequestLineId id,
        SupplyRequestId supplyRequestId,
        int lineNo,
        ProductId productId,
        decimal quantity,
        string? note)
    {
        Id = id;
        SupplyRequestId = supplyRequestId;
        LineNo = lineNo;
        ProductId = productId;
        Quantity = quantity;
        Note = note;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SupplyRequestLine()
    {
    }

    /// <summary>Gets the owning request.</summary>
    public SupplyRequestId SupplyRequestId { get; private set; }

    /// <summary>Gets the line number within the request.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the requested product.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the quantity asked for, in the product's base unit.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the requester's note for this line.</summary>
    public string? Note { get; private set; }

    /// <summary>
    /// Gets how much of this line has been placed on a purchase order. A request
    /// can be filled by more than one supplier, so this climbs towards
    /// <see cref="Quantity"/> one order at a time.
    /// </summary>
    public decimal OrderedQuantity { get; private set; }

    /// <summary>Gets what is still to be bought on this line.</summary>
    public decimal OutstandingQuantity => Quantity - OrderedQuantity;

    /// <summary>Records that part or all of this line was placed on an order.</summary>
    /// <param name="quantity">How much was ordered.</param>
    internal void RecordOrdered(decimal quantity) => OrderedQuantity += quantity;

    /// <summary>
    /// Gives back what a cancelled or rejected order had taken, so the line is
    /// outstanding again. Floored at zero: the arithmetic should never go
    /// negative, and a line claiming to be less than un-ordered would be worse
    /// than the rounding it came from.
    /// </summary>
    /// <param name="quantity">How much that order had taken.</param>
    internal void ReleaseOrdered(decimal quantity) => OrderedQuantity = Math.Max(0m, OrderedQuantity - quantity);
}

/// <summary>
/// The main warehouse asking to be restocked.
/// </summary>
/// <remarks>
/// <para>
/// A store that needs stock raises a transfer request against the warehouse,
/// because the warehouse holds the stock. The warehouse has nobody upstream: when
/// it runs low the goods have to be bought. So its request is not a transfer at
/// all — it is a shopping list the owner turns into one or more purchase orders.
/// </para>
/// <para>
/// The request deliberately carries no supplier. Choosing who to buy from is the
/// owner's decision and is made later, which is also why one request can spawn
/// several orders: the twelve products the warehouse wants may come from three
/// different suppliers.
/// </para>
/// </remarks>
public sealed class SupplyRequest : AggregateRoot<SupplyRequestId>
{
    /// <summary>The longest note or reason accepted.</summary>
    public const int TextMaxLength = 512;

    private readonly List<SupplyRequestLine> _lines = [];
    private readonly List<SupplyRequestOrder> _orders = [];

    private SupplyRequest(
        SupplyRequestId id,
        LocationId destinationLocationId,
        IReadOnlyList<SupplyRequestLine> lines,
        string? note,
        UserId requestedBy,
        DateTimeOffset requestedAtUtc)
    {
        Id = id;
        DestinationLocationId = destinationLocationId;
        _lines.AddRange(lines);
        Note = note;
        RequestedByUserId = requestedBy;
        RequestedAtUtc = requestedAtUtc;
        Status = SupplyRequestStatus.Submitted;
        Number = string.Empty;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SupplyRequest()
    {
        Number = string.Empty;
        DestinationLocationId = LocationId.Empty;
        RequestedByUserId = UserId.Empty;
    }

    /// <summary>Gets the SRQ number, assigned when the request is saved.</summary>
    public string Number { get; private set; }

    /// <summary>Gets the warehouse the goods are wanted at.</summary>
    public LocationId DestinationLocationId { get; private set; }

    /// <summary>Gets the lifecycle state.</summary>
    public SupplyRequestStatus Status { get; private set; }

    /// <summary>Gets the requester's note covering the whole request.</summary>
    public string? Note { get; private set; }

    /// <summary>Gets who raised the request.</summary>
    public UserId RequestedByUserId { get; private set; }

    /// <summary>Gets when it was raised.</summary>
    public DateTimeOffset RequestedAtUtc { get; private set; }

    /// <summary>Gets who last acted on it, once decided.</summary>
    public UserId? DecidedByUserId { get; private set; }

    /// <summary>Gets when it was last acted on.</summary>
    public DateTimeOffset? DecidedAtUtc { get; private set; }

    /// <summary>Gets why it was rejected or cancelled.</summary>
    public string? DecisionReason { get; private set; }

    /// <summary>Gets the requested lines, in order.</summary>
    public IReadOnlyList<SupplyRequestLine> Lines => _lines;

    /// <summary>Gets the purchase orders raised to fill this request.</summary>
    public IReadOnlyList<SupplyRequestOrder> Orders => _orders;

    /// <summary>Gets whether the request still awaits a decision.</summary>
    public bool IsOpen => Status is SupplyRequestStatus.Submitted or SupplyRequestStatus.PartiallyOrdered;

    /// <summary>Raises a supply request.</summary>
    /// <param name="destinationLocationId">The warehouse that wants the goods.</param>
    /// <param name="specs">What it wants.</param>
    /// <param name="note">An optional note for the owner.</param>
    /// <param name="requestedBy">Who is asking.</param>
    /// <param name="requestedAtUtc">The current instant.</param>
    /// <returns>The request, or validation errors.</returns>
    public static Result<SupplyRequest> Create(
        LocationId destinationLocationId,
        IReadOnlyList<SupplyRequestLineSpec> specs,
        string? note,
        UserId requestedBy,
        DateTimeOffset requestedAtUtc)
    {
        if (destinationLocationId.IsEmpty)
        {
            return Result<SupplyRequest>.Failure(PurchasingErrors.SupplyRequestDestinationInvalid);
        }

        if (specs is null || specs.Count == 0)
        {
            return Result<SupplyRequest>.Failure(PurchasingErrors.SupplyRequestEmpty);
        }

        if (note is { Length: > TextMaxLength })
        {
            return Result<SupplyRequest>.Failure(PurchasingErrors.SupplyRequestNoteTooLong);
        }

        List<Error> errors = [];
        List<SupplyRequestLine> lines = [];
        SupplyRequestId requestId = SupplyRequestId.New();

        for (int i = 0; i < specs.Count; i++)
        {
            SupplyRequestLineSpec spec = specs[i];
            int lineNo = i + 1;

            if (spec.ProductId.IsEmpty)
            {
                errors.Add(PurchasingErrors.SupplyRequestProductInvalid(lineNo));
                continue;
            }

            if (spec.Quantity <= 0m)
            {
                errors.Add(PurchasingErrors.SupplyRequestQuantityInvalid(lineNo));
                continue;
            }

            if (lines.Any(line => line.ProductId == spec.ProductId))
            {
                errors.Add(PurchasingErrors.SupplyRequestDuplicateLine(lineNo));
                continue;
            }

            lines.Add(new SupplyRequestLine(
                SupplyRequestLineId.New(),
                requestId,
                lineNo,
                spec.ProductId,
                spec.Quantity,
                string.IsNullOrWhiteSpace(spec.Note) ? null : spec.Note.Trim()));
        }

        if (errors.Count > 0)
        {
            return Result<SupplyRequest>.Failure(errors);
        }

        return Result<SupplyRequest>.Success(new SupplyRequest(
            requestId,
            destinationLocationId,
            lines,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            requestedBy,
            requestedAtUtc));
    }

    /// <summary>Assigns the SRQ number. Called by the repository as the request is saved.</summary>
    /// <param name="number">The allocated number.</param>
    public void AssignNumber(string number)
    {
        if (!string.IsNullOrWhiteSpace(Number))
        {
            return;
        }

        Number = number ?? string.Empty;
    }

    /// <summary>
    /// Records that a purchase order was raised against this request.
    /// </summary>
    /// <param name="purchaseOrderId">The order that was created.</param>
    /// <param name="ordered">How much of each line went onto it, by line number.</param>
    /// <param name="decidedBy">The owner placing the order.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or quantity error.</returns>
    public Result RecordPurchaseOrder(
        PurchaseOrderId purchaseOrderId,
        IReadOnlyDictionary<int, decimal> ordered,
        UserId decidedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        if (!IsOpen)
        {
            return Result.Failure(PurchasingErrors.SupplyRequestNotOpen(Status));
        }

        if (ordered.Count == 0)
        {
            return Result.Failure(PurchasingErrors.SupplyRequestEmpty);
        }

        List<Error> errors = [];

        foreach ((int lineNo, decimal quantity) in ordered)
        {
            SupplyRequestLine? line = _lines.FirstOrDefault(l => l.LineNo == lineNo);

            if (line is null)
            {
                errors.Add(PurchasingErrors.SupplyRequestLineUnknown(lineNo));
                continue;
            }

            if (quantity <= 0m)
            {
                errors.Add(PurchasingErrors.SupplyRequestQuantityInvalid(lineNo));
                continue;
            }

            // Ordering more than was asked for is allowed — a case price or a
            // minimum order quantity routinely rounds a request up — but it may
            // not exceed what is outstanding by more than the line itself, which
            // would mean the same request was ordered twice over.
            if (quantity > line.OutstandingQuantity + line.Quantity)
            {
                errors.Add(PurchasingErrors.SupplyRequestOverOrdered(lineNo, line.OutstandingQuantity));
            }
        }

        if (errors.Count > 0)
        {
            return Result.Failure(errors);
        }

        foreach ((int lineNo, decimal quantity) in ordered)
        {
            _lines.First(line => line.LineNo == lineNo).RecordOrdered(quantity);
        }

        _orders.Add(new SupplyRequestOrder(Id, purchaseOrderId, ordered, now));
        DecidedByUserId = decidedBy;
        DecidedAtUtc = now;
        Status = CurrentStatus();

        return Result.Success();
    }

    /// <summary>
    /// Gives back what a purchase order had claimed, because that order was
    /// cancelled, withdrawn or rejected and will never arrive.
    /// </summary>
    /// <param name="purchaseOrderId">The order that fell through.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>
    /// Success. Releasing an order this request never held, or has already
    /// released, is not an error: an order dies once, but the ways it can be
    /// told about are several.
    /// </returns>
    public Result ReleasePurchaseOrder(PurchaseOrderId purchaseOrderId, DateTimeOffset now)
    {
        SupplyRequestOrder? claim = _orders
            .FirstOrDefault(order => order.PurchaseOrderId == purchaseOrderId && !order.IsReleased);

        if (claim is null)
        {
            return Result.Success();
        }

        foreach (SupplyRequestOrderLine taken in claim.Lines)
        {
            _lines.FirstOrDefault(line => line.LineNo == taken.LineNo)?.ReleaseOrdered(taken.Quantity);
        }

        claim.Release(now);

        // Defensive: today a request cannot be rejected or cancelled once
        // anything has been ordered against it, so this cannot be reached. It
        // stands because the day that rule relaxes, a dead order must not drag a
        // closed request back open behind the owner's decision.
        if (Status is SupplyRequestStatus.Rejected or SupplyRequestStatus.Cancelled)
        {
            return Result.Success();
        }

        Status = CurrentStatus();
        return Result.Success();
    }

    /// <summary>Where the request stands, read off what is still to be bought.</summary>
    private SupplyRequestStatus CurrentStatus()
    {
        if (_lines.All(line => line.OutstandingQuantity <= 0m))
        {
            return SupplyRequestStatus.Ordered;
        }

        return _lines.Any(line => line.OrderedQuantity > 0m)
            ? SupplyRequestStatus.PartiallyOrdered
            : SupplyRequestStatus.Submitted;
    }

    /// <summary>Turns the request down.</summary>
    /// <param name="reason">Why. Required, so the warehouse knows where it stands.</param>
    /// <param name="decidedBy">The owner.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Reject(string? reason, UserId decidedBy, DateTimeOffset now)
    {
        if (Status != SupplyRequestStatus.Submitted)
        {
            return Result.Failure(PurchasingErrors.SupplyRequestNotOpen(Status));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            return Result.Failure(PurchasingErrors.SupplyRequestReasonRequired);
        }

        if (reason.Trim().Length > TextMaxLength)
        {
            return Result.Failure(PurchasingErrors.SupplyRequestNoteTooLong);
        }

        Status = SupplyRequestStatus.Rejected;
        DecisionReason = reason.Trim();
        DecidedByUserId = decidedBy;
        DecidedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Withdraws the request before anything has been ordered against it.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="cancelledBy">Who is withdrawing it.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Cancel(string? reason, UserId cancelledBy, DateTimeOffset now)
    {
        if (Status != SupplyRequestStatus.Submitted)
        {
            return Result.Failure(PurchasingErrors.SupplyRequestNotOpen(Status));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            return Result.Failure(PurchasingErrors.SupplyRequestReasonRequired);
        }

        Status = SupplyRequestStatus.Cancelled;
        DecisionReason = reason.Trim();
        DecidedByUserId = cancelledBy;
        DecidedAtUtc = now;
        return Result.Success();
    }
}

/// <summary>
/// A purchase order raised to fill part or all of a supply request, and how much
/// of each line it took.
/// </summary>
/// <remarks>
/// The quantities are kept because an order can die — cancelled, withdrawn or
/// rejected — and what it had claimed has to go back to the request. Without
/// them the request would stay closed over a need nobody is filling.
/// </remarks>
public sealed class SupplyRequestOrder
{
    private readonly List<SupplyRequestOrderLine> _lines = [];

    internal SupplyRequestOrder(
        SupplyRequestId supplyRequestId,
        PurchaseOrderId purchaseOrderId,
        IReadOnlyDictionary<int, decimal> ordered,
        DateTimeOffset orderedAtUtc)
    {
        SupplyRequestId = supplyRequestId;
        PurchaseOrderId = purchaseOrderId;
        OrderedAtUtc = orderedAtUtc;
        _lines.AddRange(ordered.Select(pair =>
            new SupplyRequestOrderLine(supplyRequestId, purchaseOrderId, pair.Key, pair.Value)));
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SupplyRequestOrder()
    {
    }

    /// <summary>Gets the request this order fills.</summary>
    public SupplyRequestId SupplyRequestId { get; private set; }

    /// <summary>Gets the order.</summary>
    public PurchaseOrderId PurchaseOrderId { get; private set; }

    /// <summary>Gets when it was raised.</summary>
    public DateTimeOffset OrderedAtUtc { get; private set; }

    /// <summary>Gets when the order fell through and its claim was given back.</summary>
    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    /// <summary>Gets whether this order no longer holds any of the request.</summary>
    public bool IsReleased => ReleasedAtUtc is not null;

    /// <summary>Gets how much of each request line this order took.</summary>
    public IReadOnlyList<SupplyRequestOrderLine> Lines => _lines;

    /// <summary>Marks the claim as given back. The row is kept, so the trail stands.</summary>
    /// <param name="now">The current instant.</param>
    internal void Release(DateTimeOffset now) => ReleasedAtUtc = now;
}

/// <summary>How much of one request line a purchase order took.</summary>
public sealed class SupplyRequestOrderLine
{
    internal SupplyRequestOrderLine(
        SupplyRequestId supplyRequestId,
        PurchaseOrderId purchaseOrderId,
        int lineNo,
        decimal quantity)
    {
        SupplyRequestId = supplyRequestId;
        PurchaseOrderId = purchaseOrderId;
        LineNo = lineNo;
        Quantity = quantity;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SupplyRequestOrderLine()
    {
    }

    /// <summary>Gets the request.</summary>
    public SupplyRequestId SupplyRequestId { get; private set; }

    /// <summary>Gets the order.</summary>
    public PurchaseOrderId PurchaseOrderId { get; private set; }

    /// <summary>Gets the request line this covers.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets how much of that line the order took.</summary>
    public decimal Quantity { get; private set; }
}
