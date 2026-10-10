using System.Text.Json;

namespace Pos.Web.Client;

public sealed class OfflineSnapshot
{
    public BaselineData Baseline { get; set; } = new();
    public List<OfflineCategory> Categories { get; set; } = [];
    public List<OfflineUnit> Units { get; set; } = [];
    public List<OfflineSupplier> Suppliers { get; set; } = [];
    public List<OfflineProduct> Products { get; set; } = [];
    public List<OfflineOrder> Orders { get; set; } = [];
    public OfflineStockLevels? StockLevels { get; set; }
    public DateTimeOffset DownloadedAtUtc { get; set; }
}

public sealed class OfflineStockLevels
{
    public Guid LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<OfflineStockRow> Products { get; set; } = [];
}

public sealed class OfflineStockRow
{
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Available { get; set; }
    public decimal InTransit { get; set; }
    public decimal OnHold { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class BaselineData
{
    public long Cursor { get; set; }
    public List<BaselineItem> Items { get; set; } = [];
}

public sealed class BaselineItem
{
    public string Type { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}

public sealed class OfflineProduct
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public Guid BaseUnitOfMeasureId { get; set; }
    public decimal? DefaultPurchaseCost { get; set; }
    public List<string> Barcodes { get; set; } = [];
}

public sealed class OfflineBarcode
{
    public string Barcode { get; set; } = string.Empty;
    public Guid ProductId { get; set; }
    public bool IsActive { get; set; }
}

public sealed class OfflineCategory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class OfflineUnit
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class OfflineSupplier
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class OfflineOrder
{
    public Guid Id { get; set; }
    public string? Number { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid DestinationLocationId { get; set; }
    public List<OfflineOrderLine> Lines { get; set; } = [];
}

public sealed class OfflineOrderLine
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? ProductSku { get; set; }
    public decimal OrderedQuantity { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class PurchaseWorking
{
    public Guid? SupplierId { get; set; }
    public string CustomSupplierName { get; set; } = string.Empty;
    public List<PurchaseWorkingLine> Lines { get; set; } = [];
    public List<ProposedProduct> Proposals { get; set; } = [];
}

public sealed class PurchaseWorkingLine
{
    public Guid? ProductId { get; set; }
    public Guid? ProposedProductClientId { get; set; }
    public Guid UnitOfMeasureId { get; set; }
    public decimal OrderedQuantity { get; set; } = 1;
    public decimal UnitCost { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class ProposedProduct
{
    public Guid ClientId { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid CategoryId { get; set; }
    public Guid BaseUnitOfMeasureId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public decimal DefaultPurchaseCost { get; set; }
}

public sealed class ReceivingWorking
{
    public Guid PurchaseOrderId { get; set; }
    public bool DocumentsMissing { get; set; }
    public List<ReceivingWorkingLine> Lines { get; set; } = [];
    public List<UnexpectedGood> UnexpectedGoods { get; set; } = [];
}

public sealed class ReceivingWorkingLine
{
    public Guid PurchaseOrderLineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal QuantityReceived { get; set; }
    public decimal QuantityDamaged { get; set; }
    public decimal QuantityWrongItem { get; set; }
    public decimal QuantityExpired { get; set; }
    public decimal UnitCost { get; set; }
    public string? LotNumber { get; set; }
    public DateOnly? ManufacturedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
}

public sealed class UnexpectedGood
{
    public string Barcode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
}

public sealed class RestockWorking
{
    public List<RestockWorkingLine> Lines { get; set; } = [];
}

public sealed class RestockWorkingLine
{
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
}

public sealed class OfflineOperation
{
    public Guid EventId { get; set; }
    public long Sequence { get; set; }
    public string Label { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? EntityId { get; set; }
}
