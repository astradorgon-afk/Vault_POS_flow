using Pos.Domain.Common;

namespace Pos.Domain.Inventory;

/// <summary>What a count covers.</summary>
public enum InventoryCountKind
{
    /// <summary>Every product held at the location.</summary>
    FullPhysical = 1,

    /// <summary>A scheduled subset, chosen by category or product.</summary>
    Cycle = 2,

    /// <summary>Every product in one or more categories.</summary>
    Category = 3,

    /// <summary>Named products only.</summary>
    ProductSpecific = 4,
}

/// <summary>The lifecycle state of a count.</summary>
public enum InventoryCountStatus
{
    /// <summary>Open: staff are recording counted quantities.</summary>
    Counting = 1,

    /// <summary>Every line counted; waiting for approval of the variance.</summary>
    PendingApproval = 2,

    /// <summary>Approved; the variance was posted to the ledger. Terminal.</summary>
    Posted = 3,

    /// <summary>Abandoned without posting. Terminal.</summary>
    Cancelled = 4,
}

/// <summary>A bucket placed on the count sheet when the count opens.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="BatchId">The batch, for batch-tracked products that hold one.</param>
/// <param name="SystemQuantity">The available quantity the ledger holds now.</param>
/// <param name="UnitCost">The valuation cost per unit.</param>
public sealed record InventoryCountSheetItem(
    ProductId ProductId,
    BatchId? BatchId,
    decimal SystemQuantity,
    decimal UnitCost);

/// <summary>One counted bucket.</summary>
public sealed class InventoryCountLine : Entity<InventoryCountLineId>
{
    internal InventoryCountLine(
        InventoryCountLineId id,
        InventoryCountId countId,
        int lineNo,
        ProductId productId,
        BatchId? batchId,
        decimal systemQuantity,
        decimal unitCost)
    {
        Id = id;
        InventoryCountId = countId;
        LineNo = lineNo;
        ProductId = productId;
        BatchId = batchId is { IsEmpty: false } batch ? batch : null;
        SystemQuantity = systemQuantity;
        UnitCost = unitCost;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private InventoryCountLine()
    {
    }

    /// <summary>Gets the owning count.</summary>
    public InventoryCountId InventoryCountId { get; private set; }

    /// <summary>Gets the line number.</summary>
    public int LineNo { get; private set; }

    /// <summary>Gets the product.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the batch, when batch-tracked.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>
    /// Gets the available quantity the ledger held for the bucket — at opening,
    /// then refreshed each time the line is counted, so the variance compares the
    /// shelf with the system at the same moment.
    /// </summary>
    public decimal SystemQuantity { get; private set; }

    /// <summary>Gets the counted quantity, or null while uncounted.</summary>
    public decimal? PhysicalQuantity { get; private set; }

    /// <summary>Gets the valuation cost per unit.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets who last counted the line.</summary>
    public UserId? CountedByUserId { get; private set; }

    /// <summary>Gets when the line was last counted.</summary>
    public DateTimeOffset? CountedAtUtc { get; private set; }

    /// <summary>
    /// Gets whether the same product already showed a variance at this location
    /// on another count in the look-back window, set on submission.
    /// </summary>
    public bool IsRepeatVariance { get; private set; }

    /// <summary>Gets the counted minus the system quantity, or null while uncounted. Never entered.</summary>
    public decimal? Variance => PhysicalQuantity - SystemQuantity;

    /// <summary>Gets the variance valued at the line's unit cost.</summary>
    public decimal? VarianceValue => Variance * UnitCost;

    internal void Record(decimal physicalQuantity, decimal systemQuantity, decimal unitCost, UserId countedBy, DateTimeOffset now)
    {
        PhysicalQuantity = physicalQuantity;
        SystemQuantity = systemQuantity;
        UnitCost = unitCost;
        CountedByUserId = countedBy;
        CountedAtUtc = now;
    }

    internal void MarkRepeatVariance(bool isRepeat) => IsRepeatVariance = isRepeat;
}

/// <summary>
/// A physical count of available stock at one location, posted to the ledger as
/// its variance only after approval.
/// </summary>
/// <remarks>
/// <para>
/// Opening the count places every bucket in scope on the sheet with the quantity
/// the ledger holds. Counting a line records the shelf quantity and refreshes the
/// system quantity at that moment, so a sale between opening and counting does not
/// masquerade as shrinkage. Approval refuses lines whose stock moved after they
/// were counted; they are counted again.
/// </para>
/// <para>
/// Nothing reaches the ledger before approval, and then only the variance, as
/// <c>CountAdjustmentIncrease</c>/<c>Decrease</c> movements under the CNT number:
/// the balance becomes the counted quantity because of those movements, never by
/// assignment.
/// </para>
/// </remarks>
public sealed class InventoryCount : AggregateRoot<InventoryCountId>
{
    private readonly List<InventoryCountLine> _lines = [];

    private InventoryCount(
        InventoryCountId id,
        DocumentNumber number,
        LocationId locationId,
        InventoryCountKind kind,
        string? note,
        UserId createdBy,
        DateTimeOffset now)
    {
        Id = id;
        Number = number.Value;
        LocationId = locationId;
        Kind = kind;
        Note = note;
        Status = InventoryCountStatus.Counting;
        CreatedByUserId = createdBy;
        CreatedAtUtc = now;
        SnapshotTakenAtUtc = now;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private InventoryCount()
    {
        Number = string.Empty;
    }

    /// <summary>Gets the CNT number.</summary>
    public string Number { get; private set; }

    /// <summary>Gets the counted location.</summary>
    public LocationId LocationId { get; private set; }

    /// <summary>Gets what the count covers.</summary>
    public InventoryCountKind Kind { get; private set; }

    /// <summary>Gets the lifecycle state.</summary>
    public InventoryCountStatus Status { get; private set; }

    /// <summary>Gets the opener's note, such as the aisles covered.</summary>
    public string? Note { get; private set; }

    /// <summary>Gets when the count sheet was taken from the ledger.</summary>
    public DateTimeOffset SnapshotTakenAtUtc { get; private set; }

    /// <summary>Gets who opened the count.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets when it was opened.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets who last submitted it.</summary>
    public UserId? SubmittedByUserId { get; private set; }

    /// <summary>Gets when it was last submitted.</summary>
    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    /// <summary>Gets who approved it.</summary>
    public UserId? ApprovedByUserId { get; private set; }

    /// <summary>Gets when it was approved and posted.</summary>
    public DateTimeOffset? PostedAtUtc { get; private set; }

    /// <summary>Gets why it was last sent back for recounting.</summary>
    public string? LastRejectionReason { get; private set; }

    /// <summary>Gets why it was cancelled.</summary>
    public string? CancellationReason { get; private set; }

    /// <summary>Gets the lines, in order.</summary>
    public IReadOnlyList<InventoryCountLine> Lines => _lines;

    /// <summary>Gets the total absolute variance value, which approval tiers are measured against.</summary>
    public decimal TotalAbsoluteVarianceValue => _lines.Sum(l => Math.Abs(l.VarianceValue ?? 0m));

    /// <summary>Opens a count with its sheet.</summary>
    /// <param name="number">The CNT number.</param>
    /// <param name="locationId">The counted location.</param>
    /// <param name="kind">What the count covers.</param>
    /// <param name="hasCategories">Whether categories were named.</param>
    /// <param name="hasProducts">Whether products were named.</param>
    /// <param name="sheet">The buckets in scope, with the ledger's quantities.</param>
    /// <param name="note">An optional note.</param>
    /// <param name="createdBy">Who opens it.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The open count, or a validation failure.</returns>
    public static Result<InventoryCount> Open(
        DocumentNumber number,
        LocationId locationId,
        InventoryCountKind kind,
        bool hasCategories,
        bool hasProducts,
        IReadOnlyList<InventoryCountSheetItem> sheet,
        string? note,
        UserId createdBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        Result scope = ValidateScope(kind, hasCategories, hasProducts);

        if (scope.IsFailure)
        {
            return Result<InventoryCount>.Failure(scope.Errors);
        }

        InventoryCount count = new(
            InventoryCountId.New(), number, locationId, kind,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(), createdBy, now);

        foreach (InventoryCountSheetItem item in sheet)
        {
            count.AddLine(item.ProductId, item.BatchId, item.SystemQuantity, item.UnitCost);
        }

        return Result<InventoryCount>.Success(count);
    }

    /// <summary>Checks that a kind of count was given the scope it needs.</summary>
    /// <param name="kind">What the count covers.</param>
    /// <param name="hasCategories">Whether categories were named.</param>
    /// <param name="hasProducts">Whether products were named.</param>
    /// <returns>Success, or the scope error.</returns>
    public static Result ValidateScope(InventoryCountKind kind, bool hasCategories, bool hasProducts)
    {
        bool valid = kind switch
        {
            InventoryCountKind.FullPhysical => !hasCategories && !hasProducts,
            InventoryCountKind.Category => hasCategories && !hasProducts,
            InventoryCountKind.ProductSpecific => hasProducts && !hasCategories,
            InventoryCountKind.Cycle => hasCategories || hasProducts,
            _ => false,
        };

        return valid ? Result.Success() : Result.Failure(InventoryControlErrors.CountScopeInvalid(kind));
    }

    /// <summary>
    /// Records a counted quantity for a bucket, adding a line when the bucket is not
    /// on the sheet. A scoped count only accepts products already on its sheet.
    /// </summary>
    /// <param name="productId">The product.</param>
    /// <param name="batchId">The batch, for batch-tracked products.</param>
    /// <param name="tracksBatches">Whether the product is batch-tracked.</param>
    /// <param name="physicalQuantity">The counted quantity.</param>
    /// <param name="systemQuantity">The quantity the ledger holds now.</param>
    /// <param name="unitCost">The valuation cost per unit now.</param>
    /// <param name="countedBy">Who counted.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result RecordCount(
        ProductId productId,
        BatchId? batchId,
        bool tracksBatches,
        decimal physicalQuantity,
        decimal systemQuantity,
        decimal unitCost,
        UserId countedBy,
        DateTimeOffset now)
    {
        if (Status != InventoryCountStatus.Counting)
        {
            return Result.Failure(InventoryControlErrors.CountInvalidState(InventoryCountStatus.Counting, Status));
        }

        BatchId? batch = batchId is { IsEmpty: false } b ? b : null;
        InventoryCountLine? line = _lines.FirstOrDefault(l => l.ProductId == productId && l.BatchId == batch);
        int lineNo = line?.LineNo ?? _lines.Count + 1;

        if (physicalQuantity < 0m)
        {
            return Result.Failure(InventoryControlErrors.CountQuantityNegative(lineNo));
        }

        // A batch-tracked product with no stock is listed without a batch so it can
        // be confirmed empty; anything actually found must name its batch.
        if (tracksBatches && batch is null && physicalQuantity > 0m)
        {
            return Result.Failure(InventoryControlErrors.BatchRequired(lineNo));
        }

        if (!tracksBatches && batch is not null)
        {
            return Result.Failure(InventoryControlErrors.BatchNotAllowed(lineNo));
        }

        if (line is null)
        {
            if (Kind != InventoryCountKind.FullPhysical && _lines.All(l => l.ProductId != productId))
            {
                return Result.Failure(InventoryControlErrors.CountProductNotOnSheet(productId));
            }

            line = AddLine(productId, batch, systemQuantity, unitCost);
        }

        line.Record(physicalQuantity, systemQuantity, unitCost, countedBy, now);
        return Result.Success();
    }

    /// <summary>Submits a fully counted sheet for approval.</summary>
    /// <param name="submittedBy">Who submits.</param>
    /// <param name="repeatVarianceProducts">
    /// Products that already showed a variance at this location on another count in
    /// the look-back window; their varying lines are flagged.
    /// </param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state or completeness error.</returns>
    public Result Submit(UserId submittedBy, IReadOnlySet<ProductId> repeatVarianceProducts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(repeatVarianceProducts);

        if (Status != InventoryCountStatus.Counting)
        {
            return Result.Failure(InventoryControlErrors.CountInvalidState(InventoryCountStatus.Counting, Status));
        }

        int[] uncounted = [.. _lines.Where(l => l.PhysicalQuantity is null).Select(l => l.LineNo)];

        if (_lines.Count == 0 || uncounted.Length > 0)
        {
            return Result.Failure(InventoryControlErrors.CountIncomplete(uncounted));
        }

        foreach (InventoryCountLine line in _lines)
        {
            line.MarkRepeatVariance(line.Variance != 0m && repeatVarianceProducts.Contains(line.ProductId));
        }

        Status = InventoryCountStatus.PendingApproval;
        SubmittedByUserId = submittedBy;
        SubmittedAtUtc = now;
        return Result.Success();
    }

    /// <summary>
    /// Approves the count. The caller has checked the approver's tier and that no
    /// counted bucket moved, and posts the variance movements in the same transaction.
    /// </summary>
    /// <param name="approver">Who approved it.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>Success, or a state error.</returns>
    public Result Post(UserId approver, DateTimeOffset now)
    {
        if (Status != InventoryCountStatus.PendingApproval)
        {
            return Result.Failure(InventoryControlErrors.CountInvalidState(InventoryCountStatus.PendingApproval, Status));
        }

        Status = InventoryCountStatus.Posted;
        ApprovedByUserId = approver;
        PostedAtUtc = now;
        return Result.Success();
    }

    /// <summary>Sends a submitted count back for recounting.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Reject(string? reason)
    {
        if (Status != InventoryCountStatus.PendingApproval)
        {
            return Result.Failure(InventoryControlErrors.CountInvalidState(InventoryCountStatus.PendingApproval, Status));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            return Result.Failure(InventoryControlErrors.ReasonRequired);
        }

        Status = InventoryCountStatus.Counting;
        LastRejectionReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>Abandons an open or submitted count without posting.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>Success, or a state or validation error.</returns>
    public Result Cancel(string? reason)
    {
        if (Status is not (InventoryCountStatus.Counting or InventoryCountStatus.PendingApproval))
        {
            return Result.Failure(InventoryControlErrors.CountInvalidState(InventoryCountStatus.Counting, Status));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            return Result.Failure(InventoryControlErrors.ReasonRequired);
        }

        Status = InventoryCountStatus.Cancelled;
        CancellationReason = reason.Trim();
        return Result.Success();
    }

    private InventoryCountLine AddLine(ProductId productId, BatchId? batchId, decimal systemQuantity, decimal unitCost)
    {
        InventoryCountLine line = new(
            InventoryCountLineId.New(), Id, _lines.Count + 1, productId, batchId, systemQuantity, unitCost);

        _lines.Add(line);
        return line;
    }
}

/// <summary>Identifies an inventory count line.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct InventoryCountLineId(Guid Value) : IStronglyTypedId, IComparable<InventoryCountLineId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static InventoryCountLineId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="InventoryCountLineId"/>.</returns>
    public static InventoryCountLineId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(InventoryCountLineId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
