using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Sales;

/// <summary>
/// Input to record a return line. Everything the return line freezes is read
/// from the original <see cref="SaleItem"/> snapshot — the product identity,
/// the price charged, the VAT class and the batch — so the factory performs
/// only validation and proportional arithmetic, never lookups.
/// </summary>
/// <param name="Item">The original sale line being returned against.</param>
/// <param name="Quantity">How many units of that line are accepted back.</param>
public sealed record ReturnItemSpec(SaleItem Item, decimal Quantity);

/// <summary>
/// Input to record a blind return line (POS.md §4). A blind return accepts
/// goods back with no original sale, so there is no <see cref="SaleItem"/>
/// to freeze a proportional snapshot from; the line freezes the catalog facts
/// the handler resolved — the current selling price and VAT class, the product
/// name and the valuation cost. The exchange rate of returned value is today's
/// shelf price, never a remembered one.
/// </summary>
/// <param name="ProductId">The product accepted back.</param>
/// <param name="ProductName">The frozen product name snapshot.</param>
/// <param name="Barcode">The frozen barcode snapshot, or <see langword="null"/> when the product has none.</param>
/// <param name="Quantity">How many units are accepted back, in the unit of measure.</param>
/// <param name="UnitOfMeasureId">The unit of measure the quantity is expressed in.</param>
/// <param name="UnitPrice">The current selling price, tax-inclusive, resolved at the return's location.</param>
/// <param name="VatRate">The VAT rate at the return's location, or <see langword="null"/> for exempt or zero-rated lines.</param>
/// <param name="IsVatExempt">Whether the accepted-back line is VAT-exempt.</param>
/// <param name="IsZeroRated">Whether the accepted-back line is zero-rated.</param>
/// <param name="UnitCost">The product's valuation cost per unit.</param>
public sealed record BlindReturnItemSpec(
    ProductId ProductId,
    string ProductName,
    string? Barcode,
    decimal Quantity,
    UnitOfMeasureId UnitOfMeasureId,
    decimal UnitPrice,
    decimal? VatRate,
    bool IsVatExempt,
    bool IsZeroRated,
    decimal UnitCost);

/// <summary>
/// A customer return: goods accepted back from a completed sale (POS.md §4).
/// The return freezes a proportional snapshot of every returned sale line and
/// the total value the customer may be refunded against it. Refunds are issued
/// against the return as separate child rows, each capped by what the original
/// sale paid by that method and what the return accepted back. The acceptance
/// posts the stock from the customer's External bucket into the store's
/// ReturnPending bucket; disposing of the returned goods is a later batch.
/// A <em>blind</em> return accepts goods back with no original sale under the
/// same ledger effect, plus an exception record, and is priced at the catalog
/// rather than at any remembered sale line.
/// </summary>
public class SalesReturn : AggregateRoot<SalesReturnId>
{
    /// <summary>Records a partial or full inspection decision without changing stock directly.</summary>
    /// <param name="eventId">The unique disposition event.</param>
    /// <param name="lineNumber">The return line to inspect.</param>
    /// <param name="quantity">The quantity to route.</param>
    /// <param name="kind">The inspection decision.</param>
    /// <param name="reasonCode">The ledger reason.</param>
    /// <param name="note">The inspection explanation.</param>
    /// <param name="actor">The authenticated inspector.</param>
    /// <param name="now">The server time.</param>
    /// <param name="businessDate">Today's date at the return's location.</param>
    /// <returns>The immutable decision, or the failed invariant.</returns>
    public Result<SalesReturnDisposition> DisposeLine(EventId eventId, int lineNumber, decimal quantity,
        ReturnDispositionKind kind, AdjustmentReasonCode reasonCode, string? note, UserId actor,
        DateTimeOffset now, DateOnly businessDate)
    {
        if (eventId.IsEmpty || actor.IsEmpty || now == default || businessDate == default
            || quantity <= 0m || decimal.Round(quantity, Pos.Domain.Common.Quantity.Scale) != quantity
            || !Enum.IsDefined(kind) || !Enum.IsDefined(reasonCode)
            || string.IsNullOrWhiteSpace(note) || note.Length > 512)
        {
            return Result<SalesReturnDisposition>.Failure(ReturnDispositionErrors.Invalid);
        }

        SalesReturnItem? item = _items.SingleOrDefault(i => i.LineNumber == lineNumber);
        if (item is null)
        {
            return Result<SalesReturnDisposition>.Failure(ReturnDispositionErrors.LineUnknown);
        }

        if (quantity > item.PendingDispositionQuantity)
        {
            return Result<SalesReturnDisposition>.Failure(ReturnDispositionErrors.QuantityExceeded);
        }

        if (kind == ReturnDispositionKind.Restock && item.BatchExpiresOn is { } expiry && expiry < businessDate)
        {
            return Result<SalesReturnDisposition>.Failure(ReturnDispositionErrors.Expired);
        }

        item.RecordDisposition(quantity);
        return Result<SalesReturnDisposition>.Success(new SalesReturnDisposition(
            eventId, Id, item.Id, quantity, kind, reasonCode, note.Trim(), actor, now));
    }

    /// <summary>The maximum length of a blind return's exception reason.</summary>
    public const int BlindReasonMaxLength = 200;

    private readonly List<SalesReturnItem> _items = [];
    private readonly List<Refund> _refunds = [];

    private SalesReturn(
        SalesReturnId id,
        DocumentNumber number,
        EventId eventId,
        SaleId? saleId,
        bool isBlind,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        CustomerId? customerId,
        DateOnly businessDate,
        DateTimeOffset returnedAtUtc,
        UserId returnedByUserId,
        decimal refundableTotal)
    {
        Id = id;
        Number = number.Value;
        EventId = eventId;
        SaleId = saleId;
        IsBlind = isBlind;
        LocationId = locationId;
        CashierShiftId = cashierShiftId;
        DeviceId = deviceId;
        CustomerId = customerId;
        BusinessDate = businessDate;
        ReturnedAtUtc = returnedAtUtc;
        ReturnedByUserId = returnedByUserId;
        RefundableTotal = refundableTotal;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private SalesReturn()
    {
        Number = string.Empty;
        LocationId = LocationId.Empty;
        CashierShiftId = CashierShiftId.Empty;
        DeviceId = DeviceId.Empty;
        ReturnedByUserId = UserId.Empty;
    }

    /// <summary>Gets the human-readable RET document number.</summary>
    public string Number { get; private set; }

    /// <summary>Gets the event identifier that makes the acceptance idempotent.</summary>
    public EventId EventId { get; private set; }

    /// <summary>Gets the sale the goods were originally bought under, or <see langword="null"/> for a blind return.</summary>
    public SaleId? SaleId { get; private set; }

    /// <summary>Gets a value indicating whether the return accepted goods back with no original sale.</summary>
    public bool IsBlind { get; private set; }

    /// <summary>Gets the location the return was accepted at.</summary>
    public LocationId LocationId { get; private set; }

    /// <summary>Gets the cashier shift the return was accepted in.</summary>
    public CashierShiftId CashierShiftId { get; private set; }

    /// <summary>Gets the device the return was accepted on.</summary>
    public DeviceId DeviceId { get; private set; }

    /// <summary>Gets the account customer, when the original sale was charged to a customer.</summary>
    public CustomerId? CustomerId { get; private set; }

    /// <summary>Gets the business date the return counts toward.</summary>
    public DateOnly BusinessDate { get; private set; }

    /// <summary>Gets when the goods were accepted back.</summary>
    public DateTimeOffset ReturnedAtUtc { get; private set; }

    /// <summary>Gets the user who accepted the goods back.</summary>
    public UserId ReturnedByUserId { get; private set; }

    /// <summary>
    /// Gets what the customer is owed for the accepted goods: the sum of the
    /// returned lines' proportional net values, discounts included. A refund may
    /// never push past it.
    /// </summary>
    public decimal RefundableTotal { get; private set; }

    /// <summary>Gets the return lines, in line-number order.</summary>
    public IReadOnlyList<SalesReturnItem> Items => _items;

    /// <summary>Gets the refunds issued against the return, in the order they were issued.</summary>
    public IReadOnlyList<Refund> Refunds => _refunds;

    /// <summary>Gets the value already refunded against the return.</summary>
    public decimal RefundedTotal => _refunds.Sum(r => r.Amount);

    /// <summary>
    /// Creates a customer return against a completed sale. The RET number is
    /// allocated by the caller so the document counter advances exactly once.
    /// Every line freezes a proportional snapshot of the original sale line and
    /// contributes its proportional net value to the refundable total.
    /// </summary>
    /// <param name="number">The allocated RET number.</param>
    /// <param name="eventId">The event identifier for idempotent acceptance.</param>
    /// <param name="saleId">The sale the goods were bought under.</param>
    /// <param name="locationId">The location the return was accepted at.</param>
    /// <param name="cashierShiftId">The cashier shift the return was accepted in.</param>
    /// <param name="deviceId">The device the return was accepted on.</param>
    /// <param name="customerId">The account customer, when the original sale was charged to one.</param>
    /// <param name="businessDate">The business date the return counts toward.</param>
    /// <param name="returnedAtUtc">When the goods were accepted back.</param>
    /// <param name="returnedByUserId">The user who accepted the goods back.</param>
    /// <param name="items">The lines being returned, each against a sale line.</param>
    /// <param name="alreadyReturned">
    /// How much of each sale line has been returned before this document. The
    /// caller derives it from the sale's item returned-quantity accumulators,
    /// so the aggregate never trusts the item snapshot for its caps.
    /// </param>
    /// <returns>The new return, or a validation failure.</returns>
    public static Result<SalesReturn> Create(
        DocumentNumber number,
        EventId eventId,
        SaleId saleId,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        CustomerId? customerId,
        DateOnly businessDate,
        DateTimeOffset returnedAtUtc,
        UserId returnedByUserId,
        IReadOnlyList<ReturnItemSpec> items,
        IReadOnlyDictionary<SaleItemId, decimal> alreadyReturned)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(alreadyReturned);

        Error? headerFailure = ValidateHeader(
            eventId,
            saleId,
            locationId,
            cashierShiftId,
            deviceId,
            businessDate,
            returnedAtUtc,
            returnedByUserId,
            items,
            isBlind: false);

        if (headerFailure is not null)
        {
            return Result<SalesReturn>.Failure(headerFailure);
        }

        SalesReturn candidate = new(
            SalesReturnId.New(),
            number,
            eventId,
            saleId,
            isBlind: false,
            locationId,
            cashierShiftId,
            deviceId,
            customerId,
            businessDate,
            returnedAtUtc,
            returnedByUserId,
            refundableTotal: 0m);

        List<SalesReturnItem> createdItems = [];

        for (int index = 0; index < items.Count; index++)
        {
            ReturnItemSpec spec = items[index];

            if (!alreadyReturned.TryGetValue(spec.Item.Id, out decimal previouslyReturned))
            {
                return Result<SalesReturn>.Failure(SalesReturnErrors.ItemUnknown(spec.Item.Id));
            }

            if (spec.Quantity <= 0m)
            {
                return Result<SalesReturn>.Failure(SalesReturnErrors.QuantityInvalid);
            }

            decimal remaining = decimal.Round(
                spec.Item.Quantity - previouslyReturned,
                Pos.Domain.Common.Quantity.Scale,
                MidpointRounding.ToEven);

            if (spec.Quantity > remaining)
            {
                return Result<SalesReturn>.Failure(
                    SalesReturnErrors.QuantityExceedsRemaining(spec.Item.Id, remaining, spec.Quantity));
            }

            SalesReturnItem returnItem = SalesReturnItem.Create(candidate.Id, index + 1, spec);
            createdItems.Add(returnItem);
            candidate.RefundableTotal = decimal.Round(
                candidate.RefundableTotal + returnItem.RefundableAmount,
                Money.StorageScale,
                Money.IntermediateRounding);
        }

        candidate._items.AddRange(createdItems);
        return Result<SalesReturn>.Success(candidate);
    }

    /// <summary>
    /// Creates a blind customer return: goods accepted back with no original
    /// sale (POS.md §4). Every value the referenced return freezes from a sale
    /// line, this factory freezes from the <see cref="BlindReturnItemSpec"/>
    /// snapshots the handler resolved from the catalog — the current selling
    /// price, the VAT class and the valuation cost. The reason justifies the
    /// exception record the blind path must always leave.
    /// </summary>
    /// <param name="number">The allocated RET number.</param>
    /// <param name="eventId">The event identifier for idempotent acceptance.</param>
    /// <param name="locationId">The location the return was accepted at.</param>
    /// <param name="cashierShiftId">The cashier shift the return was accepted in.</param>
    /// <param name="deviceId">The device the return was accepted on.</param>
    /// <param name="customerId">The account customer, when known.</param>
    /// <param name="businessDate">The business date the return counts toward.</param>
    /// <param name="returnedAtUtc">When the goods were accepted back.</param>
    /// <param name="returnedByUserId">The manager who accepted the goods back.</param>
    /// <param name="reason">Why a return was accepted without its original sale.</param>
    /// <param name="items">The lines being returned, each against catalog facts.</param>
    /// <returns>The new return, or a validation failure.</returns>
    public static Result<SalesReturn> CreateBlind(
        DocumentNumber number,
        EventId eventId,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        CustomerId? customerId,
        DateOnly businessDate,
        DateTimeOffset returnedAtUtc,
        UserId returnedByUserId,
        string reason,
        IReadOnlyList<BlindReturnItemSpec> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Error? headerFailure = ValidateHeader(
            eventId,
            saleId: null,
            locationId,
            cashierShiftId,
            deviceId,
            businessDate,
            returnedAtUtc,
            returnedByUserId,
            null,
            isBlind: true);

        if (headerFailure is not null)
        {
            return Result<SalesReturn>.Failure(headerFailure);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result<SalesReturn>.Failure(SalesReturnErrors.BlindReasonRequired);
        }

        if (reason.Length > BlindReasonMaxLength)
        {
            return Result<SalesReturn>.Failure(SalesReturnErrors.BlindReasonTooLong(BlindReasonMaxLength));
        }

        if (items.Count == 0)
        {
            return Result<SalesReturn>.Failure(SalesReturnErrors.ItemsRequired);
        }

        SalesReturn candidate = new(
            SalesReturnId.New(),
            number,
            eventId,
            saleId: null,
            isBlind: true,
            locationId,
            cashierShiftId,
            deviceId,
            customerId,
            businessDate,
            returnedAtUtc,
            returnedByUserId,
            refundableTotal: 0m);

        List<SalesReturnItem> createdItems = [];

        for (int index = 0; index < items.Count; index++)
        {
            BlindReturnItemSpec spec = items[index];

            if (spec.ProductId.IsEmpty)
            {
                return Result<SalesReturn>.Failure(SalesReturnErrors.ItemProductRequired);
            }

            if (spec.Quantity <= 0m)
            {
                return Result<SalesReturn>.Failure(SalesReturnErrors.QuantityInvalid);
            }

            SalesReturnItem returnItem = SalesReturnItem.CreateBlind(candidate.Id, index + 1, spec);
            createdItems.Add(returnItem);
            candidate.RefundableTotal = decimal.Round(
                candidate.RefundableTotal + returnItem.RefundableAmount,
                Money.StorageScale,
                Money.IntermediateRounding);
        }

        candidate._items.AddRange(createdItems);
        return Result<SalesReturn>.Success(candidate);
    }

    /// <summary>
    /// Issues a refund against the return (POS.md §4). A refund is capped by
    /// what this return accepted back, and per method it may never push the
    /// cumulative refunds — across every return of the original sale — past
    /// what the sale actually paid by that method.
    /// </summary>
    /// <param name="eventId">The event identifier that makes the refund idempotent.</param>
    /// <param name="cashierShiftId">The cashier shift the refund was issued in.</param>
    /// <param name="deviceId">The device the refund was issued on.</param>
    /// <param name="method">The payment method the refund is returned through.</param>
    /// <param name="amount">The amount refunded.</param>
    /// <param name="tendered">The amount handed back for cash refunds.</param>
    /// <param name="providerReference">The provider reference for card and wallet refunds.</param>
    /// <param name="refundedAtUtc">When the refund was issued.</param>
    /// <param name="refundedByUserId">The user who issued the refund.</param>
    /// <param name="originalPaidByMethod">What the original sale paid per method.</param>
    /// <param name="priorRefundedByMethod">What has already been refunded per method by earlier returns.</param>
    /// <param name="cashRoundingIncrement">The cash rounding increment, for cash refunds.</param>
    /// <returns>The new refund, or a validation failure.</returns>
    public Result<Refund> IssueRefund(
        EventId eventId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        string? providerReference,
        DateTimeOffset refundedAtUtc,
        UserId refundedByUserId,
        IReadOnlyDictionary<PaymentMethod, decimal> originalPaidByMethod,
        IReadOnlyDictionary<PaymentMethod, decimal> priorRefundedByMethod,
        decimal cashRoundingIncrement)
    {
        ArgumentNullException.ThrowIfNull(originalPaidByMethod);
        ArgumentNullException.ThrowIfNull(priorRefundedByMethod);

        if (eventId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundEventRequired);
        }

        if (cashierShiftId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundShiftRequired);
        }

        if (deviceId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundDeviceRequired);
        }

        if (refundedAtUtc == default)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundStampRequired);
        }

        if (refundedByUserId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundByRequired);
        }

        if (!Enum.IsDefined(method))
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundMethodUnknown);
        }

        if (amount <= 0m)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundAmountInvalid);
        }

        if (providerReference is { Length: > Refund.ProviderReferenceMaxLength })
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundReferenceTooLong(Refund.ProviderReferenceMaxLength));
        }

        decimal scaledAmount = decimal.Round(amount, Money.StorageScale, Money.IntermediateRounding);

        if (method == PaymentMethod.Cash)
        {
            if (tendered is null)
            {
                return Result<Refund>.Failure(SalesReturnErrors.RefundTenderedRequired);
            }

            if (cashRoundingIncrement <= 0m)
            {
                return Result<Refund>.Failure(SalesReturnErrors.RefundIncrementInvalid);
            }

            if (tendered.Value < scaledAmount)
            {
                return Result<Refund>.Failure(SalesReturnErrors.RefundTenderedInsufficient);
            }
        }

        decimal originallyPaid = originalPaidByMethod.GetValueOrDefault(method);

        if (originallyPaid <= 0m)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundMethodNotOriginal(method));
        }

        decimal refundedForMethod = decimal.Round(
            priorRefundedByMethod.GetValueOrDefault(method) + _refunds.Where(r => r.Method == method).Sum(r => r.Amount),
            Money.StorageScale,
            Money.IntermediateRounding);

        decimal requestedForMethod = decimal.Round(
            refundedForMethod + scaledAmount,
            Money.StorageScale,
            Money.IntermediateRounding);

        if (requestedForMethod > originallyPaid)
        {
            return Result<Refund>.Failure(
                SalesReturnErrors.RefundExceedsPaidForMethod(method, originallyPaid, requestedForMethod));
        }

        decimal refundedTotal = decimal.Round(RefundedTotal + scaledAmount, Money.StorageScale, Money.IntermediateRounding);

        if (refundedTotal > RefundableTotal)
        {
            return Result<Refund>.Failure(
                SalesReturnErrors.RefundExceedsRefundable(RefundableTotal, refundedTotal));
        }

        Refund refund = Refund.Create(
            Id,
            eventId,
            cashierShiftId,
            deviceId,
            method,
            scaledAmount,
            method == PaymentMethod.Cash ? tendered : null,
            providerReference?.Trim(),
            refundedAtUtc,
            refundedByUserId);

        _refunds.Add(refund);
        return Result<Refund>.Success(refund);
    }

    /// <summary>
    /// Issues a refund against a blind return (POS.md §4). A blind return
    /// accepted goods back with no original sale, so there is no per-method cap
    /// drawn from a sale's payment mix — the only cap is the return's own
    /// <see cref="RefundableTotal"/>. Because no original payment record exists
    /// to reverse, the refund is returned as cash from the drawer; cash
    /// round-trip and tendered rules still apply.
    /// </summary>
    /// <param name="eventId">The event identifier that makes the refund idempotent.</param>
    /// <param name="cashierShiftId">The cashier shift the refund was issued in.</param>
    /// <param name="deviceId">The device the refund was issued on.</param>
    /// <param name="method">The payment method the refund is returned through.</param>
    /// <param name="amount">The amount refunded.</param>
    /// <param name="tendered">The amount handed back for cash refunds.</param>
    /// <param name="providerReference">The provider reference for card and wallet refunds.</param>
    /// <param name="refundedAtUtc">When the refund was issued.</param>
    /// <param name="refundedByUserId">The user who issued the refund.</param>
    /// <param name="cashRoundingIncrement">The cash rounding increment, for cash refunds.</param>
    /// <returns>The new refund, or a validation failure.</returns>
    public Result<Refund> IssueBlindRefund(
        EventId eventId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        string? providerReference,
        DateTimeOffset refundedAtUtc,
        UserId refundedByUserId,
        decimal cashRoundingIncrement)
    {
        if (!IsBlind)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundBlindOnly);
        }

        if (eventId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundEventRequired);
        }

        if (cashierShiftId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundShiftRequired);
        }

        if (deviceId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundDeviceRequired);
        }

        if (refundedAtUtc == default)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundStampRequired);
        }

        if (refundedByUserId.IsEmpty)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundByRequired);
        }

        if (!Enum.IsDefined(method))
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundMethodUnknown);
        }

        if (amount <= 0m)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundAmountInvalid);
        }

        if (providerReference is { Length: > Refund.ProviderReferenceMaxLength })
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundReferenceTooLong(Refund.ProviderReferenceMaxLength));
        }

        decimal scaledAmount = decimal.Round(amount, Money.StorageScale, Money.IntermediateRounding);

        // A blind return has no original sale, so its refund cannot reverse an
        // original payment; it is handed back as cash from the drawer.
        if (method != PaymentMethod.Cash)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundBlindCashOnly);
        }

        if (tendered is null)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundTenderedRequired);
        }

        if (cashRoundingIncrement <= 0m)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundIncrementInvalid);
        }

        if (tendered.Value < scaledAmount)
        {
            return Result<Refund>.Failure(SalesReturnErrors.RefundTenderedInsufficient);
        }

        decimal refundedTotal = decimal.Round(RefundedTotal + scaledAmount, Money.StorageScale, Money.IntermediateRounding);

        if (refundedTotal > RefundableTotal)
        {
            return Result<Refund>.Failure(
                SalesReturnErrors.RefundExceedsRefundable(RefundableTotal, refundedTotal));
        }

        Refund refund = Refund.Create(
            Id,
            eventId,
            cashierShiftId,
            deviceId,
            method,
            scaledAmount,
            method == PaymentMethod.Cash ? tendered : null,
            providerReference?.Trim(),
            refundedAtUtc,
            refundedByUserId);

        _refunds.Add(refund);
        return Result<Refund>.Success(refund);
    }

    private static Error? ValidateHeader(
        EventId eventId,
        SaleId? saleId,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        DateOnly businessDate,
        DateTimeOffset returnedAtUtc,
        UserId returnedByUserId,
        IReadOnlyList<ReturnItemSpec>? items,
        bool isBlind)
    {
        if (eventId.IsEmpty)
        {
            return SalesReturnErrors.EventRequired;
        }

        if (!isBlind && (saleId is null || saleId.Value.IsEmpty))
        {
            return SalesReturnErrors.SaleRequired;
        }

        if (locationId.IsEmpty)
        {
            return SalesReturnErrors.LocationRequired;
        }

        if (cashierShiftId.IsEmpty)
        {
            return SalesReturnErrors.ShiftRequired;
        }

        if (deviceId.IsEmpty)
        {
            return SalesReturnErrors.DeviceRequired;
        }

        if (businessDate == default)
        {
            return SalesReturnErrors.BusinessDateRequired;
        }

        if (returnedAtUtc == default)
        {
            return SalesReturnErrors.ReturnedAtRequired;
        }

        if (returnedByUserId.IsEmpty)
        {
            return SalesReturnErrors.ReturnedByRequired;
        }

        if (!isBlind && (items is null || items.Count == 0))
        {
            return SalesReturnErrors.ItemsRequired;
        }

        return null;
    }
}
