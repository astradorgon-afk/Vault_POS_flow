using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// One payment a customer made toward a sale: cash, card, or wallet. Cash
/// records what was tendered and the change given, rounded to the location's
/// configured cash rounding increment (POS.md §2.7). Card and wallet payments
/// carry only the provider's token reference — the terminal handles the rest.
/// </summary>
public sealed class Payment : Entity<PaymentId>
{
    /// <summary>Maximum length of a provider reference.</summary>
    public const int ProviderReferenceMaxLength = 128;

    private Payment(
        PaymentId id,
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        decimal? change,
        string? providerReference)
    {
        Id = id;
        Method = method;
        Amount = amount;
        Tendered = tendered;
        Change = change;
        ProviderReference = providerReference;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Payment()
    {
        ProviderReference = null;
    }

    /// <summary>Gets the payment method.</summary>
    public PaymentMethod Method { get; private set; }

    /// <summary>Gets the amount applied to the sale.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Gets the amount tendered for cash payments, otherwise <see langword="null"/>.</summary>
    public decimal? Tendered { get; private set; }

    /// <summary>Gets the change given for cash payments, otherwise <see langword="null"/>.</summary>
    public decimal? Change { get; private set; }

    /// <summary>Gets the provider's transaction reference, for card and wallet payments.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>
    /// Creates a payment, computing cash change against the rounding increment.
    /// </summary>
    /// <param name="method">The payment method.</param>
    /// <param name="amount">The amount applied to the sale, at Money scale.</param>
    /// <param name="tendered">The amount tendered; required for cash.</param>
    /// <param name="cashRoundingIncrement">The cash rounding increment; must be positive for cash.</param>
    /// <param name="providerReference">The provider reference for card and wallet payments.</param>
    /// <returns>The payment, or a validation failure.</returns>
    internal static Result<Payment> Create(
        PaymentMethod method,
        decimal amount,
        decimal? tendered,
        decimal cashRoundingIncrement,
        string? providerReference)
    {
        if (!Enum.IsDefined(method))
        {
            return Result<Payment>.Failure(SaleErrors.PaymentMethodUnknown);
        }

        if (amount <= 0m)
        {
            return Result<Payment>.Failure(SaleErrors.PaymentAmountInvalid);
        }

        if (providerReference is { Length: > ProviderReferenceMaxLength })
        {
            return Result<Payment>.Failure(SaleErrors.PaymentReferenceTooLong(ProviderReferenceMaxLength));
        }

        if (method == PaymentMethod.Cash)
        {
            if (tendered is null)
            {
                return Result<Payment>.Failure(SaleErrors.PaymentTenderedRequired);
            }

            if (cashRoundingIncrement <= 0m)
            {
                return Result<Payment>.Failure(SaleErrors.PaymentIncrementInvalid);
            }

            decimal tenderedAmount = tendered.Value;

            if (tenderedAmount < amount)
            {
                return Result<Payment>.Failure(SaleErrors.PaymentTenderedInsufficient);
            }

            // The change is the difference between what was tendered and what was
            // applied, snapped to the smallest circulating denomination. The
            // rounding follows Money's presentation (AwayFromZero), so a
            // 999.995-peso tendering on a 0.05 increment rounds to the nearest
            // five centavos exactly as Money.RoundToCashIncrement would.
            decimal change = decimal.Round(
                (tenderedAmount - amount) / cashRoundingIncrement,
                0,
                Money.PresentationRounding) * cashRoundingIncrement;

            return Result<Payment>.Success(new Payment(
                PaymentId.New(),
                method,
                decimal.Round(amount, Money.StorageScale, Money.IntermediateRounding),
                tenderedAmount,
                decimal.Round(change, Money.StorageScale, Money.IntermediateRounding),
                providerReference));
        }

        return Result<Payment>.Success(new Payment(
            PaymentId.New(),
            method,
            decimal.Round(amount, Money.StorageScale, Money.IntermediateRounding),
            null,
            null,
            providerReference?.Trim()));
    }
}