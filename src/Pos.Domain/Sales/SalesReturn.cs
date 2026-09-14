using Pos.Domain.Common;

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
/// A customer return: goods accepted back from a completed sale (POS.md §4).
/// The return freezes a proportional snapshot of every returned sale line and
/// the total value the customer may be refunded against it. Refunds are issued
/// against the return as separate child rows, each capped by what the original
/// sale paid by that method and what the return accepted back. The acceptance
/// posts the stock from the customer's External bucket into the store's
/// ReturnPending bucket; disposing of the returned goods is a later batch.
/// </summary>
public class SalesReturn : AggregateRoot<SalesReturnId>
{
    private readonly List<SalesReturnItem> _items = [];
    private readonly List<Refund> _refunds = [];

    private SalesReturn(
        SalesReturnId id,
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
        decimal refundableTotal)
    {
        Id = id;
        Number = number.Value;
        EventId = eventId;
        SaleId = saleId;
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

    /// <summary>Gets the sale the goods were originally bought under.</summary>
    public SaleId SaleId { get; private set; }

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
            items);

        if (headerFailure is not null)
        {
            return Result<SalesReturn>.Failure(headerFailure);
        }

        SalesReturn candidate = new(
            SalesReturnId.New(),
            number,
            eventId,
            saleId,
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

    private static Error? ValidateHeader(
        EventId eventId,
        SaleId saleId,
        LocationId locationId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        DateOnly businessDate,
        DateTimeOffset returnedAtUtc,
        UserId returnedByUserId,
        IReadOnlyList<ReturnItemSpec> items)
    {
        if (eventId.IsEmpty)
        {
            return SalesReturnErrors.EventRequired;
        }

        if (saleId.IsEmpty)
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

        if (items.Count == 0)
        {
            return SalesReturnErrors.ItemsRequired;
        }

        return null;
    }
}