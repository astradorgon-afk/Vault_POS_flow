using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// One line of a completed sale. A line is a frozen snapshot of what the
/// customer bought: product identity, the price that was charged and the
/// <c>PriceVersion</c> that resolved it, the allocated batch for batch-tracked
/// products (the hook for recall traceability), the discount and who authorised
/// it, and the tax class the receipt's statutory summary needs. Nothing on a
/// line changes after completion.
/// </summary>
public sealed class SaleItem : Entity<SaleItemId>
{
    /// <summary>Maximum length of the product name snapshot.</summary>
    public const int ProductNameMaxLength = 128;

    /// <summary>Maximum length of the barcode snapshot.</summary>
    public const int BarcodeMaxLength = 48;

    private SaleItem(
        SaleItemId id,
        SaleId saleId,
        int lineNumber,
        ProductId productId,
        string productName,
        string? barcode,
        decimal quantity,
        UnitOfMeasureId unitOfMeasureId,
        decimal unitPrice,
        ProductPriceId priceVersion,
        bool priceWasOverridden,
        UserId? priceOverrideAuthorizedByUserId,
        decimal discount,
        UserId? discountAuthorizedByUserId,
        decimal? vatRate,
        bool isVatExempt,
        bool isZeroRated,
        decimal vatBase,
        decimal vat,
        BatchId? batchId,
        string? batchCode,
        DateOnly? batchExpiresOn,
        decimal unitCost)
    {
        Id = id;
        SaleId = saleId;
        LineNumber = lineNumber;
        ProductId = productId;
        ProductName = productName;
        Barcode = barcode;
        Quantity = quantity;
        UnitOfMeasureId = unitOfMeasureId;
        UnitPrice = unitPrice;
        PriceVersion = priceVersion;
        PriceWasOverridden = priceWasOverridden;
        PriceOverrideAuthorizedByUserId = priceOverrideAuthorizedByUserId;
        Discount = discount;
        DiscountAuthorizedByUserId = discountAuthorizedByUserId;
        VatRate = vatRate;
        IsVatExempt = isVatExempt;
        IsZeroRated = isZeroRated;
        VatBase = vatBase;
        Vat = vat;
        BatchId = batchId;
        BatchCode = batchCode;
        BatchExpiresOn = batchExpiresOn;
        UnitCost = unitCost;

        GrossAmount = decimal.Round(unitPrice * quantity, Money.StorageScale, Money.IntermediateRounding);
        NetAmount = decimal.Round(GrossAmount - discount, Money.StorageScale, Money.IntermediateRounding);
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SaleItem()
    {
        ProductName = string.Empty;
        SaleId = SaleId.Empty;
        ProductId = ProductId.Empty;
        UnitOfMeasureId = UnitOfMeasureId.Empty;
        PriceVersion = ProductPriceId.Empty;
    }

    /// <summary>Gets the parent sale identifier.</summary>
    public SaleId SaleId { get; private set; }

    /// <summary>Gets the sequential line number within the sale.</summary>
    public int LineNumber { get; private set; }

    /// <summary>Gets the product sold.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the frozen product name snapshot.</summary>
    public string ProductName { get; private set; }

    /// <summary>Gets the frozen barcode snapshot, or <see langword="null"/> when the product has none.</summary>
    public string? Barcode { get; private set; }

    /// <summary>Gets the quantity sold, in the unit of measure.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the unit of measure the quantity is expressed in.</summary>
    public UnitOfMeasureId UnitOfMeasureId { get; private set; }

    /// <summary>Gets the unit price charged, tax-inclusive.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>Gets the effective price row that resolved the price.</summary>
    public ProductPriceId PriceVersion { get; private set; }

    /// <summary>Gets a value indicating whether the cashier overrode the resolved price.</summary>
    public bool PriceWasOverridden { get; private set; }

    /// <summary>Gets the user who authorised a price override, when one happened.</summary>
    public UserId? PriceOverrideAuthorizedByUserId { get; private set; }

    /// <summary>Gets the discount applied to the line.</summary>
    public decimal Discount { get; private set; }

    /// <summary>Gets the user who authorised a manual discount, when one was applied.</summary>
    public UserId? DiscountAuthorizedByUserId { get; private set; }

    /// <summary>Gets the VAT rate used, for vatable lines; <see langword="null"/> for exempt or zero-rated lines.</summary>
    public decimal? VatRate { get; private set; }

    /// <summary>Gets a value indicating whether the line is VAT-exempt.</summary>
    public bool IsVatExempt { get; private set; }

    /// <summary>Gets a value indicating whether the line is zero-rated.</summary>
    public bool IsZeroRated { get; private set; }

    /// <summary>Gets the computed VAT base of the line.</summary>
    public decimal VatBase { get; private set; }

    /// <summary>Gets the computed VAT of the line.</summary>
    public decimal Vat { get; private set; }

    /// <summary>Gets the batch the units were sold from, for batch-tracked products.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>Gets the frozen batch code snapshot.</summary>
    public string? BatchCode { get; private set; }

    /// <summary>Gets the frozen batch expiry date snapshot.</summary>
    public DateOnly? BatchExpiresOn { get; private set; }

    /// <summary>Gets the cost per unit of the allocated batch.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Gets the line gross: unit price multiplied by quantity.</summary>
    public decimal GrossAmount { get; private set; }

    /// <summary>Gets the line net: gross minus the line discount.</summary>
    public decimal NetAmount { get; private set; }

    /// <summary>
    /// Gets the quantity already returned against this line across accepted
    /// returns. The parent sale keeps it current when a return records itself;
    /// a return may accept back at most <see cref="Quantity"/> minus this.
    /// </summary>
    public decimal ReturnedQuantity { get; private set; }

    /// <summary>
    /// Adds an accepted-back quantity to <see cref="ReturnedQuantity"/>. The
    /// parent sale validated the cap before calling; this only accumulates.
    /// </summary>
    /// <param name="quantity">The quantity accepted back by a return line.</param>
    internal void AccumulateReturnedQuantity(decimal quantity)
    {
        ReturnedQuantity = decimal.Round(
            ReturnedQuantity + quantity,
            Pos.Domain.Common.Quantity.Scale,
            MidpointRounding.ToEven);
    }

    /// <summary>
    /// Creates a line from a validated specification, computing the tax legs and
    /// totals. The parent aggregate validates the specification's shape first;
    /// this factory only performs arithmetic that cannot fail.
    /// </summary>
    internal static SaleItem Create(SaleId saleId, int lineNumber, ItemSpec spec)
    {
        decimal gross = decimal.Round(spec.UnitPrice * spec.Quantity, Money.StorageScale, Money.IntermediateRounding);
        decimal discount = decimal.Round(spec.Discount, Money.StorageScale, Money.IntermediateRounding);
        decimal net = decimal.Round(gross - discount, Money.StorageScale, Money.IntermediateRounding);

        decimal vatBase;
        decimal vat;

        if (spec.IsVatExempt)
        {
            vatBase = 0m;
            vat = 0m;
        }
        else if (spec.IsZeroRated)
        {
            vatBase = net;
            vat = 0m;
        }
        else
        {
            (vatBase, vat) = SalesVat.SplitTaxInclusive(net, spec.VatRate!.Value);
        }

        return new SaleItem(
            SaleItemId.New(),
            saleId,
            lineNumber,
            spec.ProductId,
            spec.ProductName.Trim(),
            spec.Barcode?.Trim(),
            decimal.Round(spec.Quantity, Pos.Domain.Common.Quantity.Scale, MidpointRounding.AwayFromZero),
            spec.UnitOfMeasureId,
            decimal.Round(spec.UnitPrice, Money.StorageScale, Money.IntermediateRounding),
            spec.PriceVersion,
            spec.PriceWasOverridden,
            spec.PriceOverrideAuthorizedByUserId,
            discount,
            spec.DiscountAuthorizedByUserId,
            spec.IsVatExempt || spec.IsZeroRated ? null : spec.VatRate,
            spec.IsVatExempt,
            spec.IsZeroRated,
            decimal.Round(vatBase, Money.StorageScale, Money.IntermediateRounding),
            decimal.Round(vat, Money.StorageScale, Money.IntermediateRounding),
            spec.BatchId,
            spec.BatchCode,
            spec.BatchExpiresOn,
            decimal.Round(spec.UnitCost, Money.StorageScale, Money.IntermediateRounding));
    }
}