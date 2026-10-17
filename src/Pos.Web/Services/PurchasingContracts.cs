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
    bool DocumentsMissing = false,
    string? SupplierDeliveryReceiptNumber = null,
    string? SupplierOfficialReceiptNumber = null,
    DateTimeOffset? SupplierReleasedAtUtc = null);

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
    decimal CostVarianceValueAtStake)
{
    /// <summary>The supplier's Delivery Receipt number, as they printed it.</summary>
    public string? SupplierDeliveryReceiptNumber { get; init; }

    /// <summary>The supplier's Official Receipt number, as they printed it.</summary>
    public string? SupplierOfficialReceiptNumber { get; init; }

    /// <summary>When the supplier released the goods, which may precede arrival.</summary>
    public DateTimeOffset? SupplierReleasedAtUtc { get; init; }
}

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
    IReadOnlyList<PosReceivingDiscrepancy> Discrepancies)
{
    /// <summary>The supplier's Delivery Receipt number, as they printed it.</summary>
    public string? SupplierDeliveryReceiptNumber { get; init; }

    /// <summary>The supplier's Official Receipt number, as they printed it.</summary>
    public string? SupplierOfficialReceiptNumber { get; init; }

    /// <summary>When the supplier released the goods, which may precede arrival.</summary>
    public DateTimeOffset? SupplierReleasedAtUtc { get; init; }
}

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

/// <summary>One product on a new warehouse supply request.</summary>
public sealed record PosSupplyRequestLine(Guid ProductId, decimal Quantity, string? Note = null);

/// <summary>The body of a new warehouse supply request.</summary>
public sealed record PosCreateSupplyRequest(
    Guid WarehouseLocationId,
    IReadOnlyList<PosSupplyRequestLine> Lines,
    string? Note = null);

/// <summary>One request line going onto a purchase order.</summary>
public sealed record PosSupplyRequestOrderLine(
    int LineNo,
    Guid UnitOfMeasureId,
    decimal Quantity,
    decimal UnitCost);

/// <summary>The body of a purchase order raised against a supply request.</summary>
public sealed record PosOrderSupplyRequest(
    Guid? SupplierId,
    IReadOnlyList<PosSupplyRequestOrderLine> Lines,
    string? CurrencyCode = null,
    DateTimeOffset? ExpectedAtUtc = null,
    string? CustomSupplierName = null);

/// <summary>One line of a supply request, as read.</summary>
public sealed record PosSupplyRequestLineView(
    int LineNo,
    Guid ProductId,
    decimal Quantity,
    decimal OrderedQuantity,
    decimal OutstandingQuantity,
    string? Note);

/// <summary>A purchase order raised against a supply request.</summary>
public sealed record PosSupplyRequestOrderView(
    Guid PurchaseOrderId,
    string? Number,
    string Status,
    DateTimeOffset OrderedAtUtc);

/// <summary>A warehouse supply request, as listed or read.</summary>
public sealed record PosSupplyRequest(
    Guid Id,
    string Number,
    Guid DestinationLocationId,
    string Status,
    string? Note,
    Guid RequestedByUserId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionReason,
    IReadOnlyList<PosSupplyRequestLineView> Lines,
    IReadOnlyList<PosSupplyRequestOrderView> Orders)
{
    /// <summary>Gets whether the request still awaits the owner.</summary>
    public bool IsOpen => Status is "Submitted" or "PartiallyOrdered";
}
