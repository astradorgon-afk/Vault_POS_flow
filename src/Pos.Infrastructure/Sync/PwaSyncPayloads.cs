namespace Pos.Infrastructure.Sync;

/// <summary>A purchase draft and its proposed products form one sync event.</summary>
public sealed record PurchaseDraftSyncPayload(
    Guid? SupplierId,
    string? CustomSupplierName,
    Guid DestinationLocationId,
    string? CurrencyCode,
    DateTimeOffset? ExpectedAtUtc,
    IReadOnlyList<PwaProposedProduct> ProposedProducts,
    IReadOnlyList<PwaPurchaseLine> Lines);

public sealed record PwaProposedProduct(
    Guid ClientId,
    string Sku,
    string Name,
    Guid CategoryId,
    Guid BaseUnitOfMeasureId,
    string? Barcode,
    decimal DefaultPurchaseCost);

public sealed record PwaPurchaseLine(
    Guid? ProductId,
    Guid? ProposedProductClientId,
    Guid UnitOfMeasureId,
    decimal OrderedQuantity,
    decimal UnitCost);

/// <summary>Physical counts against a previously downloaded order.</summary>
public sealed record PwaReceivingSyncPayload(
    Guid PurchaseOrderId,
    IReadOnlyList<PwaReceivingLine> Lines,
    IReadOnlyList<PwaUnexpectedGood> UnexpectedGoods,
    bool DocumentsMissing);

public sealed record PwaReceivingLine(
    Guid PurchaseOrderLineId,
    decimal QuantityReceived,
    decimal QuantityDamaged,
    decimal QuantityWrongItem,
    decimal QuantityExpired,
    decimal UnitCost,
    string? LotNumber,
    DateOnly? ManufacturedOn,
    DateOnly? ExpiresOn);

public sealed record PwaUnexpectedGood(string Barcode, string Description, decimal Quantity);
