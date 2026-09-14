using Pos.Domain.Inventory;

namespace Pos.Api.Endpoints;

/// <summary>The body of a new stock adjustment.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="Reason">Why the stock changes.</param>
/// <param name="Lines">The changes.</param>
/// <param name="Notes">Notes; required for the reason <c>Other</c>.</param>
public sealed record CreateStockAdjustmentBody(
    Guid LocationId,
    AdjustmentReasonCode Reason,
    IReadOnlyList<StockAdjustmentLineBody> Lines,
    string? Notes = null);

/// <summary>One change on a new stock adjustment.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="State">The bucket's state.</param>
/// <param name="QuantityDelta">The signed change; negative removes stock.</param>
/// <param name="BatchId">The batch, for batch-tracked products.</param>
public sealed record StockAdjustmentLineBody(Guid ProductId, InventoryState State, decimal QuantityDelta, Guid? BatchId = null);

/// <summary>A reason given for a rejection, reversal or cancellation.</summary>
/// <param name="Reason">Why.</param>
public sealed record InventoryControlReasonBody(string? Reason);

/// <summary>A stock adjustment as listed.</summary>
public sealed record StockAdjustmentSummary(
    Guid Id,
    string Number,
    Guid LocationId,
    string Reason,
    string Status,
    decimal TotalAbsoluteValue,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc);

/// <summary>A stock adjustment with its lines.</summary>
public sealed record StockAdjustmentDetail(
    StockAdjustmentSummary Adjustment,
    string? Notes,
    DateTimeOffset? SubmittedAtUtc,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAtUtc,
    string? RejectionReason,
    Guid? ReversedByUserId,
    DateTimeOffset? ReversedAtUtc,
    string? ReversalReason,
    IReadOnlyList<StockAdjustmentLineView> Lines);

/// <summary>A stock adjustment line.</summary>
public sealed record StockAdjustmentLineView(
    int LineNo,
    Guid ProductId,
    Guid? BatchId,
    string State,
    decimal QuantityDelta,
    decimal UnitCost,
    decimal AbsoluteValue,
    string MovementType);

/// <summary>The body of a new inventory count.</summary>
/// <param name="LocationId">The location.</param>
/// <param name="Kind">What the count covers.</param>
/// <param name="CategoryIds">The categories, for category and cycle counts.</param>
/// <param name="ProductIds">The products, for product and cycle counts.</param>
/// <param name="Note">An optional note.</param>
public sealed record OpenInventoryCountBody(
    Guid LocationId,
    InventoryCountKind Kind,
    IReadOnlyList<Guid>? CategoryIds = null,
    IReadOnlyList<Guid>? ProductIds = null,
    string? Note = null);

/// <summary>Counted quantities.</summary>
/// <param name="Lines">The counted buckets.</param>
public sealed record RecordCountLinesBody(IReadOnlyList<CountLineBody> Lines);

/// <summary>One counted bucket.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="PhysicalQuantity">The counted quantity.</param>
/// <param name="BatchId">The batch, for batch-tracked products.</param>
public sealed record CountLineBody(Guid ProductId, decimal PhysicalQuantity, Guid? BatchId = null);

/// <summary>An inventory count as listed.</summary>
public sealed record InventoryCountSummary(
    Guid Id,
    string Number,
    Guid LocationId,
    string Kind,
    string Status,
    int Lines,
    int CountedLines,
    decimal TotalAbsoluteVarianceValue,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PostedAtUtc);

/// <summary>An inventory count with its lines.</summary>
public sealed record InventoryCountDetail(
    InventoryCountSummary Count,
    string? Note,
    DateTimeOffset SnapshotTakenAtUtc,
    Guid CreatedByUserId,
    Guid? SubmittedByUserId,
    Guid? ApprovedByUserId,
    string? LastRejectionReason,
    string? CancellationReason,
    IReadOnlyList<InventoryCountLineView> Lines);

/// <summary>An inventory count line.</summary>
public sealed record InventoryCountLineView(
    int LineNo,
    Guid ProductId,
    Guid? BatchId,
    decimal SystemQuantity,
    decimal? PhysicalQuantity,
    decimal? Variance,
    decimal UnitCost,
    decimal? VarianceValue,
    bool IsRepeatVariance,
    DateTimeOffset? CountedAtUtc);

/// <summary>A variance found by a posted count.</summary>
public sealed record CountVarianceRow(
    Guid CountId,
    string CountNumber,
    DateTimeOffset PostedAtUtc,
    Guid LocationId,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    Guid? BatchId,
    decimal SystemQuantity,
    decimal PhysicalQuantity,
    decimal Variance,
    decimal VarianceValue,
    bool IsRepeatVariance);

/// <summary>A product that varied on more than one posted count at a location.</summary>
public sealed record RepeatVarianceRow(
    Guid LocationId,
    Guid ProductId,
    string? Sku,
    string? ProductName,
    int Occurrences,
    decimal NetVariance,
    decimal TotalAbsoluteVarianceValue,
    DateTimeOffset LastPostedAtUtc);
