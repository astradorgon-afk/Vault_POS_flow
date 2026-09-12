namespace Pos.Domain.Common;

/// <summary>Identifies an organization.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct OrganizationId(Guid Value) : IStronglyTypedId, IComparable<OrganizationId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static OrganizationId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="OrganizationId"/>.</returns>
    public static OrganizationId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(OrganizationId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a location (warehouse, store, or external counterparty).</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct LocationId(Guid Value) : IStronglyTypedId, IComparable<LocationId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static LocationId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="LocationId"/>.</returns>
    public static LocationId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(LocationId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a user.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct UserId(Guid Value) : IStronglyTypedId, IComparable<UserId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static UserId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="UserId"/>.</returns>
    public static UserId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(UserId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a role.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct RoleId(Guid Value) : IStronglyTypedId, IComparable<RoleId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static RoleId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="RoleId"/>.</returns>
    public static RoleId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(RoleId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a registered device.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct DeviceId(Guid Value) : IStronglyTypedId, IComparable<DeviceId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static DeviceId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="DeviceId"/>.</returns>
    public static DeviceId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(DeviceId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a supplier.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct SupplierId(Guid Value) : IStronglyTypedId, IComparable<SupplierId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static SupplierId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="SupplierId"/>.</returns>
    public static SupplierId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(SupplierId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a product.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct ProductId(Guid Value) : IStronglyTypedId, IComparable<ProductId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static ProductId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="ProductId"/>.</returns>
    public static ProductId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(ProductId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a product barcode row.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct ProductBarcodeId(Guid Value) : IStronglyTypedId, IComparable<ProductBarcodeId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static ProductBarcodeId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="ProductBarcodeId"/>.</returns>
    public static ProductBarcodeId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(ProductBarcodeId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a product category.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct CategoryId(Guid Value) : IStronglyTypedId, IComparable<CategoryId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static CategoryId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="CategoryId"/>.</returns>
    public static CategoryId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(CategoryId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a brand.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct BrandId(Guid Value) : IStronglyTypedId, IComparable<BrandId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static BrandId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="BrandId"/>.</returns>
    public static BrandId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(BrandId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a unit of measure.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct UnitOfMeasureId(Guid Value) : IStronglyTypedId, IComparable<UnitOfMeasureId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static UnitOfMeasureId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="UnitOfMeasureId"/>.</returns>
    public static UnitOfMeasureId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(UnitOfMeasureId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a batch or lot.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct BatchId(Guid Value) : IStronglyTypedId, IComparable<BatchId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static BatchId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="BatchId"/>.</returns>
    public static BatchId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(BatchId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies an effective-dated product price row.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct ProductPriceId(Guid Value) : IStronglyTypedId, IComparable<ProductPriceId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static ProductPriceId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="ProductPriceId"/>.</returns>
    public static ProductPriceId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(ProductPriceId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a product unit-of-measure conversion row.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct ProductUnitConversionId(Guid Value) : IStronglyTypedId, IComparable<ProductUnitConversionId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static ProductUnitConversionId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="ProductUnitConversionId"/>.</returns>
    public static ProductUnitConversionId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(ProductUnitConversionId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies per-location settings for a product.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct ProductLocationSettingId(Guid Value) : IStronglyTypedId, IComparable<ProductLocationSettingId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static ProductLocationSettingId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="ProductLocationSettingId"/>.</returns>
    public static ProductLocationSettingId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(ProductLocationSettingId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a product-to-supplier link.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct ProductSupplierId(Guid Value) : IStronglyTypedId, IComparable<ProductSupplierId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static ProductSupplierId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="ProductSupplierId"/>.</returns>
    public static ProductSupplierId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(ProductSupplierId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a purchase order.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct PurchaseOrderId(Guid Value) : IStronglyTypedId, IComparable<PurchaseOrderId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static PurchaseOrderId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="PurchaseOrderId"/>.</returns>
    public static PurchaseOrderId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(PurchaseOrderId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a purchase order line.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct PurchaseOrderLineId(Guid Value) : IStronglyTypedId, IComparable<PurchaseOrderLineId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static PurchaseOrderLineId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="PurchaseOrderLineId"/>.</returns>
    public static PurchaseOrderLineId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(PurchaseOrderLineId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a goods receipt.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct GoodsReceiptId(Guid Value) : IStronglyTypedId, IComparable<GoodsReceiptId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static GoodsReceiptId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="GoodsReceiptId"/>.</returns>
    public static GoodsReceiptId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(GoodsReceiptId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a goods receipt line.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct GoodsReceiptLineId(Guid Value) : IStronglyTypedId, IComparable<GoodsReceiptLineId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static GoodsReceiptLineId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="GoodsReceiptLineId"/>.</returns>
    public static GoodsReceiptLineId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(GoodsReceiptLineId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a transfer order.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct TransferOrderId(Guid Value) : IStronglyTypedId, IComparable<TransferOrderId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static TransferOrderId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="TransferOrderId"/>.</returns>
    public static TransferOrderId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(TransferOrderId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a transfer order line.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct TransferOrderLineId(Guid Value) : IStronglyTypedId, IComparable<TransferOrderLineId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static TransferOrderLineId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="TransferOrderLineId"/>.</returns>
    public static TransferOrderLineId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(TransferOrderLineId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a transfer shipment.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct TransferShipmentId(Guid Value) : IStronglyTypedId, IComparable<TransferShipmentId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static TransferShipmentId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="TransferShipmentId"/>.</returns>
    public static TransferShipmentId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(TransferShipmentId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a transfer receipt.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct TransferReceiptId(Guid Value) : IStronglyTypedId, IComparable<TransferReceiptId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static TransferReceiptId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="TransferReceiptId"/>.</returns>
    public static TransferReceiptId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(TransferReceiptId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies an inventory movement leg.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct InventoryMovementId(Guid Value) : IStronglyTypedId, IComparable<InventoryMovementId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static InventoryMovementId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="InventoryMovementId"/>.</returns>
    public static InventoryMovementId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(InventoryMovementId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a group of inventory movement legs forming one event.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct MovementGroupId(Guid Value) : IStronglyTypedId, IComparable<MovementGroupId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static MovementGroupId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="MovementGroupId"/>.</returns>
    public static MovementGroupId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(MovementGroupId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a globally unique business event.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct EventId(Guid Value) : IStronglyTypedId, IComparable<EventId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static EventId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="EventId"/>.</returns>
    public static EventId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(EventId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies an inventory count.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct InventoryCountId(Guid Value) : IStronglyTypedId, IComparable<InventoryCountId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static InventoryCountId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="InventoryCountId"/>.</returns>
    public static InventoryCountId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(InventoryCountId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a stock adjustment.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct StockAdjustmentId(Guid Value) : IStronglyTypedId, IComparable<StockAdjustmentId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static StockAdjustmentId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="StockAdjustmentId"/>.</returns>
    public static StockAdjustmentId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(StockAdjustmentId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a quarantine incident.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct QuarantineIncidentId(Guid Value) : IStronglyTypedId, IComparable<QuarantineIncidentId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static QuarantineIncidentId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="QuarantineIncidentId"/>.</returns>
    public static QuarantineIncidentId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(QuarantineIncidentId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a sale.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct SaleId(Guid Value) : IStronglyTypedId, IComparable<SaleId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static SaleId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="SaleId"/>.</returns>
    public static SaleId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(SaleId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a sale line.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct SaleItemId(Guid Value) : IStronglyTypedId, IComparable<SaleItemId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static SaleItemId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="SaleItemId"/>.</returns>
    public static SaleItemId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(SaleItemId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a payment.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct PaymentId(Guid Value) : IStronglyTypedId, IComparable<PaymentId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static PaymentId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="PaymentId"/>.</returns>
    public static PaymentId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(PaymentId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a cashier shift.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct CashierShiftId(Guid Value) : IStronglyTypedId, IComparable<CashierShiftId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static CashierShiftId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="CashierShiftId"/>.</returns>
    public static CashierShiftId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(CashierShiftId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a customer.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct CustomerId(Guid Value) : IStronglyTypedId, IComparable<CustomerId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static CustomerId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="CustomerId"/>.</returns>
    public static CustomerId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(CustomerId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a customer return.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct SalesReturnId(Guid Value) : IStronglyTypedId, IComparable<SalesReturnId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static SalesReturnId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="SalesReturnId"/>.</returns>
    public static SalesReturnId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(SalesReturnId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a notification.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct NotificationId(Guid Value) : IStronglyTypedId, IComparable<NotificationId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static NotificationId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="NotificationId"/>.</returns>
    public static NotificationId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(NotificationId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies an audit entry.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct AuditLogId(Guid Value) : IStronglyTypedId, IComparable<AuditLogId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static AuditLogId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="AuditLogId"/>.</returns>
    public static AuditLogId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(AuditLogId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Identifies a correlated chain of operations.</summary>
/// <param name="Value">The underlying UUIDv7 value.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1036:Override methods on comparable types",
    Justification = "Identifier ordering is used only for deterministic sorting.")]
public readonly record struct CorrelationId(Guid Value) : IStronglyTypedId, IComparable<CorrelationId>
{
    /// <summary>Gets the unassigned value.</summary>
    public static CorrelationId Empty => new(Guid.Empty);

    /// <summary>Creates a new time-ordered identifier.</summary>
    /// <returns>A new <see cref="CorrelationId"/>.</returns>
    public static CorrelationId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this identifier is unassigned.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <inheritdoc />
    public int CompareTo(CorrelationId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
