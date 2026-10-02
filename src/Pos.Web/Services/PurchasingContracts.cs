namespace Pos.Web.Services;

/// <summary>A supplier as listed for purchasing.</summary>
public sealed record PosSupplier(
    Guid Id,
    string Code,
    string Name,
    string? TaxId,
    int PaymentTermsDays,
    int LeadTimeDays,
    bool IsActive);

/// <summary>A catalogue product as offered to the purchase order composer.</summary>
/// <remarks>The cost is null when the signed-in user may not see costs.</remarks>
public sealed record PosOrderableProduct(
    Guid Id,
    string Sku,
    string Name,
    Guid? PrimarySupplierId,
    Guid BaseUnitOfMeasureId,
    decimal? DefaultPurchaseCost,
    bool TracksBatches,
    bool TracksExpiry,
    bool IsActive,
    IReadOnlyList<string>? Barcodes = null);

/// <summary>A product found by scanning its barcode.</summary>
public sealed record PosScannedProduct(Guid Id, string Sku, string Name, bool IsActive);

/// <summary>A unit of measure an order line can be counted in.</summary>
public sealed record PosUnitOfMeasure(Guid Id, string Code, string Name, int DecimalPlaces);

/// <summary>A purchase order as listed.</summary>
public sealed record PosPurchaseOrderSummary(
    Guid Id,
    string? Number,
    string Status,
    Guid SupplierId,
    Guid DestinationLocationId,
    decimal GrandTotal,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? OrderedAtUtc,
    decimal OutstandingQuantity = 0m);

/// <summary>One ordered line.</summary>
public sealed record PosPurchaseOrderLine(
    Guid Id,
    int LineNo,
    Guid ProductId,
    Guid UnitOfMeasureId,
    decimal OrderedQuantity,
    decimal UnitCost,
    decimal LineTotal,
    string? ProductSku = null,
    string? ProductName = null);

/// <summary>An approval, rejection or cancellation recorded on an order.</summary>
public sealed record PosPurchaseApproval(
    Guid ApproverUserId,
    string Decision,
    DateTimeOffset DecidedAtUtc,
    string? Notes,
    decimal ThresholdApplied);

/// <summary>A purchase order with its lines and decisions.</summary>
public sealed record PosPurchaseOrder(
    Guid Id,
    string? Number,
    string Status,
    Guid SupplierId,
    Guid DestinationLocationId,
    string CurrencyCode,
    decimal Subtotal,
    decimal TaxTotal,
    decimal GrandTotal,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? OrderedAtUtc,
    DateTimeOffset? ExpectedAtUtc,
    string? CancelledReason,
    string? ClosedReason,
    IReadOnlyList<PosPurchaseOrderLine> Lines,
    IReadOnlyList<PosPurchaseApproval> Approvals)
{
    public string? ReceivingTimeZoneId { get; init; }
}

/// <summary>The body that creates a draft purchase order.</summary>
public sealed record PosCreatePurchaseOrder(
    Guid? SupplierId,
    Guid DestinationLocationId,
    IReadOnlyList<PosCreatePurchaseOrderLine> Lines,
    string? CurrencyCode = null,
    DateTimeOffset? ExpectedAtUtc = null,
    string? CustomSupplierName = null);

/// <summary>One line of a new purchase order, in the product's base unit.</summary>
public sealed record PosCreatePurchaseOrderLine(
    Guid ProductId,
    Guid UnitOfMeasureId,
    decimal OrderedQuantity,
    decimal UnitCost);

/// <summary>The body that records one delivery against an order.</summary>
public sealed record PosCreateGoodsReceipt(
    IReadOnlyList<PosCreateGoodsReceiptLine> Lines,
    bool DocumentsMissing = false);

/// <summary>What arrived for one order line in this delivery.</summary>
public sealed record PosCreateGoodsReceiptLine(
    Guid PurchaseOrderLineId,
    decimal QuantityReceived,
    decimal QuantityDamaged,
    decimal QuantityWrongItem,
    decimal QuantityExpired,
    decimal UnitCost,
    string? LotNumber = null,
    DateOnly? ManufacturedOn = null,
    DateOnly? ExpiresOn = null);

/// <summary>A delivery recorded against an order, as listed.</summary>
public sealed record PosGoodsReceiptSummary(
    Guid Id,
    string Number,
    string Status,
    Guid PurchaseOrderId,
    Guid SupplierId,
    Guid DestinationLocationId,
    bool DocumentsMissing,
    DateOnly BusinessDate,
    DateTimeOffset ReceivedAtUtc,
    Guid ReceivedByUserId,
    bool CostVariancePendingApproval,
    decimal CostVarianceValueAtStake);

/// <summary>One line of a recorded delivery.</summary>
public sealed record PosGoodsReceiptLine(
    int LineNo,
    Guid PurchaseOrderLineId,
    Guid ProductId,
    decimal QuantityExpected,
    decimal QuantityReceived,
    decimal QuantityDamaged,
    decimal QuantityWrongItem,
    decimal QuantityExpired,
    decimal OverageBeyondTolerance,
    decimal QuantityAccepted,
    string AcceptedState,
    decimal UnitCost,
    string? LotNumber,
    DateOnly? ManufacturedOn,
    DateOnly? ExpiresOn,
    decimal CostVariancePercent,
    Guid? CostVarianceApprovedByUserId,
    DateTimeOffset? CostVarianceApprovedAtUtc);

/// <summary>A shortfall, damage or other difference found while receiving.</summary>
public sealed record PosReceivingDiscrepancy(
    Guid Id,
    Guid PurchaseOrderLineId,
    int LineNo,
    string Kind,
    decimal Quantity,
    decimal ValueImpact,
    string? ResolutionOutcome = null,
    string? ResolutionNote = null,
    Guid? ResolvedByUserId = null,
    DateTimeOffset? ResolvedAtUtc = null);

/// <summary>A recorded delivery with its lines and discrepancies.</summary>
public sealed record PosGoodsReceipt(
    Guid Id,
    string Number,
    string Status,
    Guid PurchaseOrderId,
    Guid SupplierId,
    Guid DestinationLocationId,
    bool DocumentsMissing,
    DateOnly BusinessDate,
    DateTimeOffset ReceivedAtUtc,
    Guid ReceivedByUserId,
    bool CostVariancePendingApproval,
    decimal CostVarianceValueAtStake,
    IReadOnlyList<PosGoodsReceiptLine> Lines,
    IReadOnlyList<PosReceivingDiscrepancy> Discrepancies);

/// <summary>Best- and worst-selling products over a period.</summary>
public sealed record PosProductPerformanceReport(
    DateOnly From,
    DateOnly To,
    string RankBy,
    PosStoreProductPerformance Business,
    IReadOnlyList<PosStoreProductPerformance> Stores);

/// <summary>One store's (or the whole business's) best and worst sellers.</summary>
public sealed record PosStoreProductPerformance(
    Guid LocationId,
    string Code,
    string Name,
    decimal NetSales,
    decimal UnitsSold,
    int ProductsSold,
    int ProductsNotSold,
    IReadOnlyList<PosProductRank> Best,
    IReadOnlyList<PosProductRank> Worst);

/// <summary>One product's place in a best or worst ranking.</summary>
public sealed record PosProductRank(
    int Rank,
    Guid ProductId,
    string Sku,
    string Name,
    string Category,
    decimal Quantity,
    decimal NetSales,
    decimal Share,
    decimal OnHand);
