using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// One refund issued against a customer return (POS.md §4): money returned
/// through exactly one of the methods the original sale was paid with. Cash
/// refunds record the amount handed back; card and wallet refunds carry the
/// provider's token reference. The refund's events, shift, device and operator
/// are frozen so a shift declaration can attribute the drawer's cash refunds
/// back to the shift that issued them.
/// </summary>
public sealed class Refund : Entity<RefundId>
{
    /// <summary>Maximum length of a provider reference.</summary>
    public const int ProviderReferenceMaxLength = 128;

    private Refund(
        RefundId id,
        SalesReturnId salesReturnId,
        EventId eventId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        string? providerReference,
        DateTimeOffset refundedAtUtc,
        UserId refundedByUserId)
    {
        Id = id;
        SalesReturnId = salesReturnId;
        EventId = eventId;
        CashierShiftId = cashierShiftId;
        DeviceId = deviceId;
        Method = method;
        Amount = amount;
        Tendered = tendered;
        ProviderReference = providerReference;
        RefundedAtUtc = refundedAtUtc;
        RefundedByUserId = refundedByUserId;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Refund()
    {
        SalesReturnId = SalesReturnId.Empty;
        CashierShiftId = CashierShiftId.Empty;
        DeviceId = DeviceId.Empty;
        RefundedByUserId = UserId.Empty;
    }

    /// <summary>Gets the parent return identifier.</summary>
    public SalesReturnId SalesReturnId { get; private set; }

    /// <summary>Gets the event identifier that makes the refund idempotent.</summary>
    public EventId EventId { get; private set; }

    /// <summary>Gets the cashier shift the refund was issued in.</summary>
    public CashierShiftId CashierShiftId { get; private set; }

    /// <summary>Gets the device the refund was issued on.</summary>
    public DeviceId DeviceId { get; private set; }

    /// <summary>Gets the payment method the refund is returned through.</summary>
    public PaymentMethod Method { get; private set; }

    /// <summary>Gets the amount refunded.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Gets the amount handed back for cash refunds, otherwise <see langword="null"/>.</summary>
    public decimal? Tendered { get; private set; }

    /// <summary>Gets the provider's transaction reference, for card and wallet refunds.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>Gets when the refund was issued.</summary>
    public DateTimeOffset RefundedAtUtc { get; private set; }

    /// <summary>Gets the user who issued the refund.</summary>
    public UserId RefundedByUserId { get; private set; }

    /// <summary>
    /// Creates a refund from values the parent aggregate already validated and
    /// scaled. The parent <see cref="SalesReturn"/> checks the per-method caps,
    /// the refundable total and the cash rules; this factory only snapshots.
    /// </summary>
    internal static Refund Create(
        SalesReturnId salesReturnId,
        EventId eventId,
        CashierShiftId cashierShiftId,
        DeviceId deviceId,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        string? providerReference,
        DateTimeOffset refundedAtUtc,
        UserId refundedByUserId) => new(
        RefundId.New(),
        salesReturnId,
        eventId,
        cashierShiftId,
        deviceId,
        method,
        amount,
        tendered,
        providerReference,
        refundedAtUtc,
        refundedByUserId);
}