namespace Pos.Web.Services;

/// <summary>Goods discovered while receiving that must be quarantined.</summary>
public sealed record PosCreateQuarantineLine(string Barcode, decimal Quantity, decimal? UnitCost = null, string? ClaimedProductName = null);

/// <summary>Raises one quarantine incident at the receiving location.</summary>
public sealed record PosCreateQuarantineIncident(Guid LocationId, IReadOnlyList<PosCreateQuarantineLine> Lines, string? Note);

/// <summary>An inventory count as listed in the dashboard.</summary>
public sealed record PosInventoryCountSummary(
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

/// <summary>An inventory count with its recorded lines.</summary>
public sealed record PosInventoryCountDetail(
    PosInventoryCountSummary Count,
    string? Note,
    DateTimeOffset SnapshotTakenAtUtc,
    Guid CreatedByUserId,
    Guid? SubmittedByUserId,
    Guid? ApprovedByUserId,
    string? LastRejectionReason,
    string? CancellationReason,
    IReadOnlyList<PosInventoryCountLineView> Lines);

/// <summary>One counted bucket on an inventory count sheet.</summary>
public sealed record PosInventoryCountLineView(
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

/// <summary>A product category, used to scope a category or cycle count.</summary>
public sealed class PosCategory
{
    /// <summary>Gets or sets the category identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the category code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the category name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the category is active.</summary>
    public bool IsActive { get; set; }
}

/// <summary>A product master row used to name count-sheet lines.</summary>
public sealed record PosProductSummary(
    Guid Id,
    string Sku,
    string Name,
    bool IsActive);

/// <summary>The body that opens a stock count.</summary>
public sealed record PosOpenInventoryCountRequest(
    Guid LocationId,
    int Kind,
    IReadOnlyList<Guid>? CategoryIds,
    string? Note);

/// <summary>One counted quantity sent for a count line.</summary>
public sealed record PosCountLineRequest(Guid ProductId, decimal PhysicalQuantity, Guid? BatchId = null);

/// <summary>The body that records counted quantities.</summary>
public sealed record PosRecordCountLinesRequest(IReadOnlyList<PosCountLineRequest> Lines);

/// <summary>A reason attached to a count rejection or cancellation.</summary>
public sealed record PosInventoryControlReasonRequest(string? Reason);

/// <summary>A transfer as listed in the transfers dashboard.</summary>
public sealed record PosTransferSummary(
    Guid Id,
    string Number,
    string Status,
    Guid SourceLocationId,
    Guid DestinationLocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ReceivedAtUtc,
    decimal TotalValue,
    int LineCount,
    string Mode = "Normal");

/// <summary>A store's request for supplies from the main warehouse.</summary>
public sealed record PosCreateRestockRequest(
    Guid StoreLocationId,
    IReadOnlyList<PosTransferLineRequest> Lines,
    Guid? WarehouseLocationId = null);

/// <summary>A transfer with its lines, allocations and arrival state.</summary>
public sealed record PosTransferDetail(
    PosTransferSummary Transfer,
    string Kind,
    string Mode,
    string? ReviewNote,
    Guid? ApprovedByUserId,
    string? ApprovedByDisplayName,
    DateTimeOffset? ApprovedAtUtc,
    IReadOnlyList<PosTransferLineView> Lines);

/// <summary>One line of a transfer detail.</summary>
public sealed record PosTransferLineView(
    int LineNo,
    Guid ProductId,
    string? ProductName,
    decimal RequestedQuantity,
    decimal PickedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity,
    string? Note,
    string? DiscrepancyState,
    IReadOnlyList<PosTransferAllocationView> Allocations);

/// <summary>One picked lot within a transfer line.</summary>
public sealed record PosTransferAllocationView(
    Guid? BatchId,
    decimal PickedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);

/// <summary>One arrival receipt line, keyed to a dispatched allocation.</summary>
public sealed record PosTransferReceiveLine(
    int LineNo,
    Guid? BatchId,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);

/// <summary>The body that records a transfer arrival.</summary>
public sealed record PosReceiveTransferRequest(IReadOnlyList<PosTransferReceiveLine> Receives);

/// <summary>One line of a transfer request.</summary>
public sealed record PosTransferLineRequest(Guid ProductId, decimal Quantity, string? Note = null);

/// <summary>The body that raises a transfer request.</summary>
public sealed record PosCreateTransferRequest(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<PosTransferLineRequest> Lines,
    Guid? PreApprovalTokenId = null);

/// <summary>An approval-time quantity change.</summary>
public sealed record PosTransferAmendment(int LineNo, decimal RequestedQuantity);

/// <summary>The body of a transfer approval, optionally amending quantities.</summary>
public sealed record PosApproveTransferRequest(
    IReadOnlyList<PosTransferAmendment>? Amendments = null,
    string? Note = null);

/// <summary>One picked lot of a transfer.</summary>
public sealed record PosTransferPickLine(Guid? BatchId, int LineNo, decimal Quantity);

/// <summary>The body recording what was picked.</summary>
public sealed record PosPickTransferRequest(IReadOnlyList<PosTransferPickLine> Allocations);

/// <summary>The body cancelling a dispatch.</summary>
public sealed record PosCancelTransferDispatchRequest(string Reason);

/// <summary>A note attached to a review, rejection or cancellation.</summary>
public sealed record PosTransferReasonRequest(string? Note);

/// <summary>One step in a transfer's custody timeline.</summary>
public sealed record PosTransferCustodyEventSummary(
    int Sequence,
    string Kind,
    Guid ActorUserId,
    DateTimeOffset OccurredAtUtc,
    string? Note);

/// <summary>The movement chain for one inventory document.</summary>
public sealed class PosInventoryTimeline
{
    /// <summary>Gets or sets the document type, for example Sale or Transfer.</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>Gets or sets the document identifier.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>Gets or sets the human-readable document number.</summary>
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets the movement groups that make up the chain.</summary>
    public List<PosInventoryTimelineGroup> Groups { get; set; } = [];
}

/// <summary>One movement group in an inventory timeline.</summary>
public sealed class PosInventoryTimelineGroup
{
    /// <summary>Gets or sets the movement group identifier.</summary>
    public Guid MovementGroupId { get; set; }

    /// <summary>Gets or sets the movement type, for example Post, Adjustment or Dispatch.</summary>
    public string MovementType { get; set; } = string.Empty;

    /// <summary>Gets or sets the reference number of the owning document.</summary>
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets when the movement occurred.</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Gets or sets when the movement was recorded.</summary>
    public DateTimeOffset RecordedAtUtc { get; set; }

    /// <summary>Gets or sets the legs of the movement group.</summary>
    public List<PosInventoryTimelineLeg> Legs { get; set; } = [];
}

/// <summary>One leg of an inventory movement group.</summary>
public sealed class PosInventoryTimelineLeg
{
    /// <summary>Gets or sets the individual movement identifier.</summary>
    public Guid MovementId { get; set; }

    /// <summary>Gets or sets the leg number within the group.</summary>
    public short LegNumber { get; set; }

    /// <summary>Gets or sets the product.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Gets or sets the product display name.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Gets or sets the location the leg applied to.</summary>
    public Guid LocationId { get; set; }

    /// <summary>Gets or sets the bucket state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the signed quantity change.</summary>
    public decimal QuantityDelta { get; set; }

    /// <summary>Gets or sets the per-unit cost at the time of the movement.</summary>
    public decimal UnitCost { get; set; }

    /// <summary>Gets or sets the source location for a dispatch.</summary>
    public Guid? SourceLocationId { get; set; }

    /// <summary>Gets or sets the destination location for an arrival.</summary>
    public Guid? DestinationLocationId { get; set; }

    /// <summary>Gets or sets the movement group this leg reverses.</summary>
    public Guid? ReversesMovementGroupId { get; set; }
}

