using Pos.Domain.Common;

namespace Pos.Domain.Catalog;

/// <summary>
/// A scannable barcode attached to a product. Barcodes are globally unique
/// across the catalogue: two products sharing a barcode would silently corrupt
/// both stock and revenue, so re-pointing an existing barcode is rejected in the
/// aggregate and enforced by a database unique constraint.
/// </summary>
/// <remarks>
/// A barcode is never deleted. Retiring it keeps the row, so history that
/// scanned it still resolves, and keeps the value reserved: a retired code is
/// never re-pointed at another product.
/// </remarks>
public sealed class ProductBarcode
{
    internal ProductBarcode(
        ProductBarcodeId id,
        ProductId productId,
        Barcode barcode,
        UnitOfMeasureId unitOfMeasureId,
        decimal packQuantity,
        bool isPrimary,
        UserId createdByUserId)
    {
        Id = id;
        ProductId = productId;
        Value = barcode.Value;
        Symbology = barcode.Symbology;
        UnitOfMeasureId = unitOfMeasureId;
        PackQuantity = packQuantity;
        IsPrimary = isPrimary;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private ProductBarcode()
        => Value = string.Empty;

    /// <summary>Gets the barcode row identifier.</summary>
    public ProductBarcodeId Id { get; }

    /// <summary>Gets the owning product.</summary>
    public ProductId ProductId { get; }

    /// <summary>Gets the barcode value and symbology.</summary>
    /// <remarks>Re-derived from the persisted value, whose symbology is deterministic.</remarks>
    public Barcode Barcode => Catalog.Barcode.FromTrustedSource(Value);

    /// <summary>Gets the persisted barcode value.</summary>
    public string Value { get; private set; }

    /// <summary>Gets the detected symbology.</summary>
    public BarcodeSymbology Symbology { get; private set; }

    /// <summary>Gets the unit of measure this code scans as.</summary>
    public UnitOfMeasureId UnitOfMeasureId { get; }

    /// <summary>Gets how many base units this code represents (for example 24 pieces per case).</summary>
    public decimal PackQuantity { get; }

    /// <summary>Gets whether this is the primary code for the product.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>Gets who attached the code.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Gets when the code was attached.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets when the code was retired, or null while it is in use.</summary>
    public DateTimeOffset? RetiredAtUtc { get; private set; }

    /// <summary>Gets who retired the code, if it is retired.</summary>
    public UserId? RetiredByUserId { get; private set; }

    /// <summary>Gets whether the code has been retired and no longer scans.</summary>
    public bool IsRetired => RetiredAtUtc is not null;

    internal void DemotePrimary() => IsPrimary = false;

    internal void PromotePrimary() => IsPrimary = true;

    internal void Retire(UserId retiredByUserId, DateTimeOffset retiredAtUtc)
    {
        RetiredAtUtc = retiredAtUtc;
        RetiredByUserId = retiredByUserId;
        IsPrimary = false;
    }
}