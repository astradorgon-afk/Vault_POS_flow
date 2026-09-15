using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// One line of a customer return. A referenced return freezes a proportional
/// snapshot of the original sale line — the multiplier is the returned quantity
/// over the sale quantity, so a half-returned line half-refunds its gross,
/// discount, VAT legs and net. A blind return freezes the catalog facts the
/// handler resolved instead: the full line at today's price, with no
/// <see cref="SaleItemId"/> because no sale line exists. The snapshot keeps the
/// batch identity and unit cost where the source had them, for recall
/// traceability and return valuation.
/// </summary>
public sealed class SalesReturnItem : Entity<SalesReturnItemId>
{
    /// <summary>Maximum length of the product name snapshot.</summary>
    public const int ProductNameMaxLength = 128;

    /// <summary>Maximum length of the barcode snapshot.</summary>
    public const int BarcodeMaxLength = 48;

    private SalesReturnItem(
        SalesReturnItemId id,
        SalesReturnId salesReturnId,
        int lineNumber,
        SaleItemId? saleItemId,
        ProductId productId,
        string productName,
        string? barcode,
        decimal quantity,
        UnitOfMeasureId unitOfMeasureId,
        decimal unitPrice,
        decimal grossAmount,
        decimal discount,
        decimal netAmount,
        decimal refundableAmount,
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
        SalesReturnId = salesReturnId;
        LineNumber = lineNumber;
        SaleItemId = saleItemId;
        ProductId = productId;
        ProductName = productName;
        Barcode = barcode;
        Quantity = quantity;
        UnitOfMeasureId = unitOfMeasureId;
        UnitPrice = unitPrice;
        GrossAmount = grossAmount;
        Discount = discount;
        NetAmount = netAmount;
        RefundableAmount = refundableAmount;
        VatRate = vatRate;
        IsVatExempt = isVatExempt;
        IsZeroRated = isZeroRated;
        VatBase = vatBase;
        Vat = vat;
        BatchId = batchId;
        BatchCode = batchCode;
        BatchExpiresOn = batchExpiresOn;
        UnitCost = unitCost;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SalesReturnItem()
    {
        ProductName = string.Empty;
        SalesReturnId = SalesReturnId.Empty;
        ProductId = ProductId.Empty;
        UnitOfMeasureId = UnitOfMeasureId.Empty;
    }

    /// <summary>Gets the parent return identifier.</summary>
    public SalesReturnId SalesReturnId { get; private set; }

    /// <summary>Gets the sequential line number within the return.</summary>
    public int LineNumber { get; private set; }

    /// <summary>Gets the original sale line this return line accepts back against, or <see langword="null"/> for a blind return.</summary>
    public SaleItemId? SaleItemId { get; private set; }

    /// <summary>Gets the product accepted back.</summary>
    public ProductId ProductId { get; private set; }

    /// <summary>Gets the frozen product name snapshot.</summary>
    public string ProductName { get; private set; }

    /// <summary>Gets the frozen barcode snapshot, or <see langword="null"/> when the product has none.</summary>
    public string? Barcode { get; private set; }

    /// <summary>Gets the quantity accepted back, in the unit of measure.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Gets the unit of measure the quantity is expressed in.</summary>
    public UnitOfMeasureId UnitOfMeasureId { get; private set; }

    /// <summary>Gets the unit price the original sale charged, tax-inclusive.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>Gets the proportional line gross: unit price multiplied by the returned quantity.</summary>
    public decimal GrossAmount { get; private set; }

    /// <summary>Gets the proportional line discount.</summary>
    public decimal Discount { get; private set; }

    /// <summary>Gets the proportional line net: proportional gross minus proportional discount.</summary>
    public decimal NetAmount { get; private set; }

    /// <summary>Gets what a refund may pay for this line: the proportional net value.</summary>
    public decimal RefundableAmount { get; private set; }

    /// <summary>Gets the VAT rate used, for vatable lines; <see langword="null"/> for exempt or zero-rated lines.</summary>
    public decimal? VatRate { get; private set; }

    /// <summary>Gets a value indicating whether the accepted-back line is VAT-exempt.</summary>
    public bool IsVatExempt { get; private set; }

    /// <summary>Gets a value indicating whether the accepted-back line is zero-rated.</summary>
    public bool IsZeroRated { get; private set; }

    /// <summary>Gets the proportional VAT base of the line.</summary>
    public decimal VatBase { get; private set; }

    /// <summary>Gets the proportional VAT of the line.</summary>
    public decimal Vat { get; private set; }

    /// <summary>Gets the batch the accepted-back units came from, for batch-tracked products.</summary>
    public BatchId? BatchId { get; private set; }

    /// <summary>Gets the frozen batch code snapshot.</summary>
    public string? BatchCode { get; private set; }

    /// <summary>Gets the frozen batch expiry date snapshot.</summary>
    public DateOnly? BatchExpiresOn { get; private set; }

    /// <summary>Gets the cost per unit of the batch the goods came back from.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>
    /// Creates the proportional snapshot of a returned sale line. The parent
    /// aggregate validated the quantity cap; this factory only performs the
    /// proportional arithmetic, which cannot fail.
    /// </summary>
    internal static SalesReturnItem Create(SalesReturnId salesReturnId, int lineNumber, ReturnItemSpec spec)
    {
        SaleItem item = spec.Item;
        decimal quantity = decimal.Round(spec.Quantity, Pos.Domain.Common.Quantity.Scale, MidpointRounding.AwayFromZero);
        decimal fraction = spec.Quantity / item.Quantity;

        decimal gross = decimal.Round(item.GrossAmount * fraction, Money.StorageScale, Money.IntermediateRounding);
        decimal discount = decimal.Round(item.Discount * fraction, Money.StorageScale, Money.IntermediateRounding);
        decimal net = decimal.Round(item.NetAmount * fraction, Money.StorageScale, Money.IntermediateRounding);
        decimal vatBase = decimal.Round(item.VatBase * fraction, Money.StorageScale, Money.IntermediateRounding);
        decimal vat = decimal.Round(item.Vat * fraction, Money.StorageScale, Money.IntermediateRounding);

        return new SalesReturnItem(
            SalesReturnItemId.New(),
            salesReturnId,
            lineNumber,
            item.Id,
            item.ProductId,
            item.ProductName,
            item.Barcode,
            quantity,
            item.UnitOfMeasureId,
            item.UnitPrice,
            gross,
            discount,
            net,
            net,
            item.IsVatExempt || item.IsZeroRated ? null : item.VatRate,
            item.IsVatExempt,
            item.IsZeroRated,
            vatBase,
            vat,
            item.BatchId,
            item.BatchCode,
            item.BatchExpiresOn,
            item.UnitCost);
    }

    /// <summary>
    /// Creates the full-value snapshot of a blind return line (POS.md §4). The
    /// parent aggregate validated the quantity; this factory records the
    /// catalog facts the handler resolved — value at today's price, valuation
    /// at the product's default cost, no batch identity and no sale line.
    /// </summary>
    internal static SalesReturnItem CreateBlind(SalesReturnId salesReturnId, int lineNumber, BlindReturnItemSpec spec)
    {
        decimal quantity = decimal.Round(spec.Quantity, Pos.Domain.Common.Quantity.Scale, MidpointRounding.AwayFromZero);
        decimal gross = decimal.Round(spec.UnitPrice * quantity, Money.StorageScale, Money.IntermediateRounding);
        decimal net = gross;

        // The vatable branch splits the tax-inclusive net exactly like a sale
        // line does, so blind returns and sales agree on the base-to-VAT split
        // for the same shelf price.
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

        return new SalesReturnItem(
            SalesReturnItemId.New(),
            salesReturnId,
            lineNumber,
            saleItemId: null,
            spec.ProductId,
            spec.ProductName,
            spec.Barcode,
            quantity,
            spec.UnitOfMeasureId,
            spec.UnitPrice,
            gross,
            discount: 0m,
            net,
            net,
            spec.IsVatExempt || spec.IsZeroRated ? null : spec.VatRate,
            spec.IsVatExempt,
            spec.IsZeroRated,
            decimal.Round(vatBase, Money.StorageScale, Money.IntermediateRounding),
            decimal.Round(vat, Money.StorageScale, Money.IntermediateRounding),
            batchId: null,
            batchCode: null,
            batchExpiresOn: null,
            spec.UnitCost);
    }
}