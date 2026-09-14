using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// Input to create a sale line. Everything the line freezes is supplied here —
/// the resolved price and its <c>PriceVersion</c>, the FEFO-allocated batch, the
/// tax class and rate — so the factory performs only validation and arithmetic,
/// never lookups.
/// </summary>
/// <param name="ProductId">The product sold.</param>
/// <param name="ProductName">The product name snapshot.</param>
/// <param name="Barcode">The barcode snapshot, when the product has one.</param>
/// <param name="Quantity">The quantity sold, in the unit of measure.</param>
/// <param name="UnitOfMeasureId">The unit of measure the quantity is expressed in.</param>
/// <param name="UnitPrice">The unit price charged, tax-inclusive.</param>
/// <param name="PriceVersion">The effective price row that resolved the price.</param>
/// <param name="PriceWasOverridden">Whether the cashier overrode the resolved price.</param>
/// <param name="PriceOverrideAuthorizedByUserId">The user who authorised an override, when one happened.</param>
/// <param name="Discount">The line discount.</param>
/// <param name="DiscountAuthorizedByUserId">The user who authorised a manual discount, when one was applied.</param>
/// <param name="VatRate">The VAT rate as a fraction, required for vatable lines.</param>
/// <param name="IsVatExempt">Whether the line is VAT-exempt.</param>
/// <param name="IsZeroRated">Whether the line is zero-rated.</param>
/// <param name="BatchId">The FEFO-allocated batch, required for batch-tracked products.</param>
/// <param name="BatchCode">The batch code snapshot.</param>
/// <param name="BatchExpiresOn">The batch expiry date snapshot.</param>
/// <param name="UnitCost">The cost per unit of the allocated batch.</param>
/// <param name="TracksBatches">Whether the product tracks batches.</param>
public sealed record ItemSpec(
    ProductId ProductId,
    string ProductName,
    string? Barcode,
    decimal Quantity,
    UnitOfMeasureId UnitOfMeasureId,
    decimal UnitPrice,
    ProductPriceId PriceVersion,
    bool PriceWasOverridden,
    UserId? PriceOverrideAuthorizedByUserId,
    decimal Discount,
    UserId? DiscountAuthorizedByUserId,
    decimal? VatRate,
    bool IsVatExempt,
    bool IsZeroRated,
    BatchId? BatchId,
    string? BatchCode,
    DateOnly? BatchExpiresOn,
    decimal UnitCost,
    bool TracksBatches);

/// <summary>Input to create a payment.</summary>
/// <param name="Method">The payment method.</param>
/// <param name="Amount">The amount applied to the sale.</param>
/// <param name="Tendered">The amount tendered; required for cash.</param>
/// <param name="ProviderReference">The provider reference for card and wallet payments.</param>
public sealed record PaymentSpec(
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);

/// <summary>
/// A completed sale: the frozen record of what was sold, for how much, and how
/// it was paid. The sale, its lines, and its payments are written in one atomic
/// transaction together with the ledger movements that take the goods off the
/// shelf (POS.md §3). The document number and <see cref="EventId"/> are
/// allocated by the caller so the counter advances exactly once and a retried
/// completion returns the original result.
/// </summary>
public class Sale : AggregateRoot<SaleId>
{
    /// <summary>Default cash rounding increment when the location has none configured.</summary>
    public const decimal DefaultCashRoundingIncrement = 0.01m;

    /// <summary>Maximum length of the free-text reason recorded when a sale is voided.</summary>
    public const int VoidReasonMaxLength = 200;

    private readonly List<SaleItem> _items = [];
    private readonly List<Payment> _payments = [];

    private Sale(
        SaleId id,
        DocumentNumber number,
        EventId eventId,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        CustomerId? customerId,
        DateOnly businessDate,
        DateTimeOffset completedAtUtc,
        UserId completedByUserId,
        decimal grossTotal,
        decimal discountTotal,
        decimal netTotal,
        decimal vatTotal,
        decimal vatExemptTotal,
        decimal zeroRatedTotal,
        decimal taxableBaseTotal)
    {
        Id = id;
        Number = number.Value;
        Status = SaleStatus.Completed;
        EventId = eventId;
        LocationId = locationId;
        CashierShiftId = cashierShiftId;
        DeviceId = deviceId;
        CustomerId = customerId;
        BusinessDate = businessDate;
        CompletedAtUtc = completedAtUtc;
        CompletedByUserId = completedByUserId;
        GrossTotal = grossTotal;
        DiscountTotal = discountTotal;
        NetTotal = netTotal;
        VatTotal = vatTotal;
        VatExemptTotal = vatExemptTotal;
        ZeroRatedTotal = zeroRatedTotal;
        TaxableBaseTotal = taxableBaseTotal;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Sale()
    {
        Number = string.Empty;
        LocationId = LocationId.Empty;
        CashierShiftId = CashierShiftId.Empty;
        DeviceId = DeviceId.Empty;
        CompletedByUserId = UserId.Empty;
    }

    /// <summary>Gets the human-readable SAL document number.</summary>
    public string Number { get; private set; }

    /// <summary>Gets the lifecycle status.</summary>
    public SaleStatus Status { get; private set; }

    /// <summary>Gets the event identifier that makes completion idempotent.</summary>
    public EventId EventId { get; private set; }

    /// <summary>Gets the location the sale happened at.</summary>
    public LocationId LocationId { get; private set; }

    /// <summary>Gets the cashier shift the sale belongs to.</summary>
    public CashierShiftId CashierShiftId { get; private set; }

    /// <summary>Gets the device the sale was completed on.</summary>
    public DeviceId DeviceId { get; private set; }

    /// <summary>Gets the account customer, when the sale was charged to a customer.</summary>
    public CustomerId? CustomerId { get; private set; }

    /// <summary>Gets the business date the sale counts toward.</summary>
    public DateOnly BusinessDate { get; private set; }

    /// <summary>Gets when the sale was completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; private set; }

    /// <summary>Gets the cashier who completed the sale.</summary>
    public UserId CompletedByUserId { get; private set; }

    /// <summary>Gets when the sale was voided, when it was.</summary>
    public DateTimeOffset? VoidedAtUtc { get; private set; }

    /// <summary>Gets the user who voided the sale, when it was.</summary>
    public UserId? VoidedByUserId { get; private set; }

    /// <summary>Gets the reason recorded for the void, when one was given.</summary>
    public string? VoidReason { get; private set; }

    /// <summary>Gets the sum of all line gross amounts.</summary>
    public decimal GrossTotal { get; private set; }

    /// <summary>Gets the sum of all line discounts.</summary>
    public decimal DiscountTotal { get; private set; }

    /// <summary>Gets the total due: gross minus discounts. Payments must cover it exactly.</summary>
    public decimal NetTotal { get; private set; }

    /// <summary>Gets the sum of all line VAT.</summary>
    public decimal VatTotal { get; private set; }

    /// <summary>Gets the total of VAT-exempt lines, for the receipt's statutory summary.</summary>
    public decimal VatExemptTotal { get; private set; }

    /// <summary>Gets the total of zero-rated lines, for the receipt's statutory summary.</summary>
    public decimal ZeroRatedTotal { get; private set; }

    /// <summary>Gets the sum of the VAT bases of vatable lines.</summary>
    public decimal TaxableBaseTotal { get; private set; }

    /// <summary>Gets the sale lines, in line-number order.</summary>
    public IReadOnlyList<SaleItem> Items => _items;

    /// <summary>Gets the payments, in the order they were recorded.</summary>
    public IReadOnlyList<Payment> Payments => _payments;

    /// <summary>
    /// Creates a completed sale. The SAL number is allocated by the caller so the
    /// document counter advances exactly once, in the same transaction as the
    /// sale. The factory validates the document shape, computes the per-line tax
    /// and the document totals, and enforces that the payments cover the total
    /// due exactly.
    /// </summary>
    /// <param name="number">The allocated SAL number.</param>
    /// <param name="eventId">The event identifier for idempotent completion.</param>
    /// <param name="locationId">The location the sale happened at.</param>
    /// <param name="cashierShiftId">The cashier shift the sale belongs to.</param>
    /// <param name="deviceId">The device the sale was completed on.</param>
    /// <param name="customerId">The account customer, when the sale was charged to a customer.</param>
    /// <param name="businessDate">The business date the sale counts toward.</param>
    /// <param name="completedAtUtc">When the sale was completed.</param>
    /// <param name="completedByUserId">The cashier who completed the sale.</param>
    /// <param name="items">The lines to sell.</param>
    /// <param name="payments">The payments that settle the sale.</param>
    /// <param name="cashRoundingIncrement">The cash rounding increment for cash change.</param>
    /// <returns>The new sale, or a validation failure.</returns>
    public static Result<Sale> Create(
        DocumentNumber number,
        EventId eventId,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        CustomerId? customerId,
        DateOnly businessDate,
        DateTimeOffset completedAtUtc,
        UserId completedByUserId,
        IReadOnlyList<ItemSpec> items,
        IReadOnlyList<PaymentSpec> payments,
        decimal cashRoundingIncrement = DefaultCashRoundingIncrement)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(payments);

        Error? headerFailure = ValidateHeader(
            eventId,
            locationId,
            cashierShiftId,
            deviceId,
            businessDate,
            completedAtUtc,
            completedByUserId,
            items,
            payments);

        if (headerFailure is not null)
        {
            return Result<Sale>.Failure(headerFailure);
        }

        Sale candidate = new(
            SaleId.New(),
            number,
            eventId,
            locationId,
            cashierShiftId,
            deviceId,
            customerId,
            businessDate,
            completedAtUtc,
            completedByUserId,
            grossTotal: 0m,
            discountTotal: 0m,
            netTotal: 0m,
            vatTotal: 0m,
            vatExemptTotal: 0m,
            zeroRatedTotal: 0m,
            taxableBaseTotal: 0m);

        List<SaleItem> createdItems = [];

        for (int index = 0; index < items.Count; index++)
        {
            ItemSpec spec = items[index];

            Error? itemFailure = ValidateItem(spec);

            if (itemFailure is not null)
            {
                return Result<Sale>.Failure(itemFailure);
            }

            SaleItem item = SaleItem.Create(candidate.Id, index + 1, spec);
            createdItems.Add(item);

            candidate.GrossTotal += item.GrossAmount;
            candidate.DiscountTotal += item.Discount;
            candidate.NetTotal += item.NetAmount;
            candidate.VatTotal += item.Vat;
            candidate.TaxableBaseTotal += item.IsVatExempt || item.IsZeroRated ? 0m : item.VatBase;

            if (item.IsVatExempt)
            {
                candidate.VatExemptTotal += item.NetAmount;
            }

            if (item.IsZeroRated)
            {
                candidate.ZeroRatedTotal += item.NetAmount;
            }
        }

        decimal netTotal = decimal.Round(candidate.NetTotal, Money.StorageScale, Money.IntermediateRounding);
        decimal paymentsTotal = 0m;

        foreach (PaymentSpec spec in payments)
        {
            Result<Payment> payment = Payment.Create(
                spec.Method,
                spec.Amount,
                spec.Tendered,
                cashRoundingIncrement,
                spec.ProviderReference);

            if (payment.IsFailure)
            {
                return Result<Sale>.Failure(payment.Error);
            }

            candidate._payments.Add(payment.Value);
            paymentsTotal += payment.Value.Amount;
        }

        if (decimal.Round(paymentsTotal, Money.StorageScale, Money.IntermediateRounding) != netTotal)
        {
            return Result<Sale>.Failure(SaleErrors.PaymentMismatch(netTotal, paymentsTotal));
        }

        candidate._items.AddRange(createdItems);
        return Result<Sale>.Success(candidate);
    }

    /// <summary>
    /// Voids a completed sale. Google-site rule POS.md §4: only a completed sale
    /// can be voided, it belongs to the shift and business date that complete it,
    /// and the void is stamped with who did it and why. Reversing the stock legs
    /// is the application layer's job — this aggregate only flips its own state.
    /// </summary>
    /// <param name="shiftId">The cashier shift the sale belongs to; the current shift must match.</param>
    /// <param name="businessDate">The business date checked against the sale's.</param>
    /// <param name="voidedAtUtc">When the void happened.</param>
    /// <param name="voidedByUserId">The user who authorised the void.</param>
    /// <param name="reason">Why the sale was voided.</param>
    /// <returns>The voided sale, or a validation failure.</returns>
    public Result Void(
        CashierShiftId shiftId,
        DateOnly businessDate,
        DateTimeOffset voidedAtUtc,
        UserId voidedByUserId,
        string reason)
    {
        if (Status != SaleStatus.Completed)
        {
            return Result.Failure(SaleErrors.VoidOnlyCompleted);
        }

        if (shiftId != CashierShiftId)
        {
            return Result.Failure(SaleErrors.VoidShiftMismatch);
        }

        if (businessDate != BusinessDate)
        {
            return Result.Failure(SaleErrors.VoidBusinessDateMismatch);
        }

        if (voidedAtUtc == default)
        {
            return Result.Failure(SaleErrors.VoidStampRequired);
        }

        if (voidedByUserId.IsEmpty)
        {
            return Result.Failure(SaleErrors.VoidByRequired);
        }

        string trimmedReason = reason?.Trim() ?? string.Empty;

        if (trimmedReason.Length == 0 || trimmedReason.Length > VoidReasonMaxLength)
        {
            return Result.Failure(SaleErrors.VoidReasonInvalid(VoidReasonMaxLength));
        }

        Status = SaleStatus.Voided;
        VoidedAtUtc = voidedAtUtc;
        VoidedByUserId = voidedByUserId;
        VoidReason = trimmedReason;
        return Result.Success();
    }

    /// <summary>
    /// Records that a customer return accepted back part of one line (POS.md §4).
    /// The sale's state is the server-side cap every return is checked against:
    /// each line may be returned up to the quantity originally sold, no more.
    /// Returns of voided sales are rejected here as well as by the application
    /// layer, so a return can never leak stock off a dead document.
    /// </summary>
    /// <param name="itemId">The sale line being returned against.</param>
    /// <param name="returnedQuantity">The quantity the return accepted back.</param>
    /// <returns>A success, or a validation failure.</returns>
    public Result RecordReturn(SaleItemId itemId, decimal returnedQuantity)
    {
        if (Status != SaleStatus.Completed)
        {
            return Result.Failure(SaleErrors.ReturnOnlyCompleted);
        }

        SaleItem? item = _items.FirstOrDefault(i => i.Id == itemId);

        if (item is null)
        {
            return Result.Failure(SaleErrors.ReturnItemUnknown(itemId));
        }

        if (returnedQuantity <= 0m)
        {
            return Result.Failure(SaleErrors.ReturnQuantityInvalid);
        }

        decimal remaining = decimal.Round(
            item.Quantity - item.ReturnedQuantity,
            Quantity.Scale,
            MidpointRounding.ToEven);

        if (returnedQuantity > remaining)
        {
            return Result.Failure(
                SaleErrors.ReturnQuantityExceedsRemaining(itemId, remaining, returnedQuantity));
        }

        item.AccumulateReturnedQuantity(returnedQuantity);
        return Result.Success();
    }

    private static Error? ValidateHeader(
        EventId eventId,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        DateOnly businessDate,
        DateTimeOffset completedAtUtc,
        UserId completedByUserId,
        IReadOnlyList<ItemSpec> items,
        IReadOnlyList<PaymentSpec> payments)
    {
        if (eventId.IsEmpty)
        {
            return SaleErrors.EventRequired;
        }

        if (locationId.IsEmpty)
        {
            return SaleErrors.LocationRequired;
        }

        if (cashierShiftId.IsEmpty)
        {
            return SaleErrors.ShiftRequired;
        }

        if (deviceId.IsEmpty)
        {
            return SaleErrors.DeviceRequired;
        }

        if (businessDate == default)
        {
            return SaleErrors.BusinessDateRequired;
        }

        if (completedAtUtc == default)
        {
            return SaleErrors.CompletedAtRequired;
        }

        if (completedByUserId.IsEmpty)
        {
            return SaleErrors.CashierRequired;
        }

        if (items.Count == 0)
        {
            return SaleErrors.ItemsRequired;
        }

        if (payments.Count == 0)
        {
            return SaleErrors.PaymentsRequired;
        }

        return null;
    }

    private static Error? ValidateItem(ItemSpec spec)
    {
        if (spec.ProductId.IsEmpty)
        {
            return SaleErrors.ItemProductRequired;
        }

        string productName = spec.ProductName?.Trim() ?? string.Empty;

        if (productName.Length == 0 || productName.Length > SaleItem.ProductNameMaxLength)
        {
            return SaleErrors.ItemProductNameInvalid(SaleItem.ProductNameMaxLength);
        }

        string? barcode = spec.Barcode?.Trim();

        if (barcode is { Length: > SaleItem.BarcodeMaxLength })
        {
            return SaleErrors.ItemBarcodeTooLong(SaleItem.BarcodeMaxLength);
        }

        if (spec.Quantity <= 0m)
        {
            return SaleErrors.ItemQuantityInvalid;
        }

        if (spec.UnitPrice < 0m)
        {
            return SaleErrors.ItemPriceNegative;
        }

        decimal gross = decimal.Round(spec.UnitPrice * spec.Quantity, Money.StorageScale, Money.IntermediateRounding);

        if (spec.Discount < 0m || spec.Discount > gross)
        {
            return SaleErrors.ItemDiscountInvalid;
        }

        if (spec.Discount > 0m && spec.DiscountAuthorizedByUserId is null)
        {
            return SaleErrors.ItemDiscountAuthorizerRequired;
        }

        if (spec.PriceWasOverridden && spec.PriceOverrideAuthorizedByUserId is null)
        {
            return SaleErrors.ItemPriceOverrideAuthorizerRequired;
        }

        if (spec.PriceVersion.IsEmpty && !spec.PriceWasOverridden)
        {
            return SaleErrors.ItemPriceVersionRequired;
        }

        if (spec.IsVatExempt && spec.IsZeroRated)
        {
            return SaleErrors.ItemVatClassConflict;
        }

        if (!spec.IsVatExempt && !spec.IsZeroRated && (spec.VatRate is null || spec.VatRate.Value <= 0m))
        {
            return SaleErrors.ItemVatRateMissing;
        }

        if (spec.TracksBatches && (spec.BatchId is null || spec.BatchId.Value.IsEmpty))
        {
            return SaleErrors.ItemBatchMissing;
        }

        if (!spec.TracksBatches && spec.BatchId is not null)
        {
            return SaleErrors.ItemBatchUnexpected;
        }

        return null;
    }
}