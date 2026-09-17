using System.Globalization;

namespace Pos.Domain.Common;

/// <summary>
/// A monetary amount in a specific currency. Always <see cref="decimal"/>:
/// binary floating point is never used for money anywhere in this system.
/// </summary>
/// <remarks>
/// Amounts are held at <see cref="StorageScale"/> decimal places, matching the
/// database column type. Presentation rounds once, at the document boundary,
/// using <see cref="PresentationRounding"/>.
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Decimal places retained in storage and intermediate arithmetic.</summary>
    /// <summary>
    /// The currency every amount in this system is held in. Prices, costs and
    /// totals are stored in it by configuration, and the change feed carries it
    /// down so a register states its currency rather than assuming one. It lives
    /// here, on money, because that is what it is a fact about.
    /// </summary>
    public const string DefaultCurrency = "PHP";

    public const int StorageScale = 4;

    /// <summary>Decimal places used when presenting or settling an amount.</summary>
    public const int PresentationScale = 2;

    /// <summary>Rounding applied when reducing to <see cref="PresentationScale"/>.</summary>
    public const MidpointRounding PresentationRounding = MidpointRounding.AwayFromZero;

    /// <summary>Rounding applied to intermediate values such as weighted average cost.</summary>
    public const MidpointRounding IntermediateRounding = MidpointRounding.ToEven;

    /// <summary>Initializes a new instance of the <see cref="Money"/> struct.</summary>
    /// <param name="amount">The amount; rounded to <see cref="StorageScale"/> places.</param>
    /// <param name="currency">ISO-4217 alphabetic currency code.</param>
    /// <exception cref="ArgumentException">The currency code is not three letters.</exception>
    public Money(decimal amount, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Currency must be a three-letter ISO-4217 code; got {currency}."),
                nameof(currency));
        }

        Amount = decimal.Round(amount, StorageScale, IntermediateRounding);
        Currency = currency.ToUpperInvariant();
    }

    /// <summary>Gets the amount, at <see cref="StorageScale"/> decimal places.</summary>
    public decimal Amount { get; }

    /// <summary>Gets the ISO-4217 currency code.</summary>
    public string Currency { get; }

    /// <summary>Gets a value indicating whether the amount is zero.</summary>
    public bool IsZero => Amount == 0m;

    /// <summary>Gets a value indicating whether the amount is negative.</summary>
    public bool IsNegative => Amount < 0m;

    /// <summary>Creates a zero amount in the given currency.</summary>
    /// <param name="currency">ISO-4217 alphabetic currency code.</param>
    /// <returns>Zero money.</returns>
    public static Money Zero(string currency) => new(0m, currency);

    /// <summary>Rounds to <see cref="PresentationScale"/> for display or settlement.</summary>
    /// <returns>The rounded amount.</returns>
    public Money ToPresentation()
        => new(decimal.Round(Amount, PresentationScale, PresentationRounding), Currency);

    /// <summary>Returns the absolute value.</summary>
    /// <returns>The absolute amount.</returns>
    public Money Abs() => new(Math.Abs(Amount), Currency);

    /// <summary>Rounds to a cash denomination increment, such as 0.01 or 0.05.</summary>
    /// <param name="increment">The smallest circulating denomination. Must be positive.</param>
    /// <returns>The rounded amount.</returns>
    public Money RoundToCashIncrement(decimal increment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(increment);
        decimal steps = decimal.Round(Amount / increment, 0, PresentationRounding);
        return new Money(steps * increment, Currency);
    }

    /// <summary>Allocates this amount across weights without losing or inventing cents.</summary>
    /// <param name="weights">Relative weights; must be non-negative and sum to more than zero.</param>
    /// <returns>Amounts whose sum equals this amount exactly at presentation scale.</returns>
    /// <remarks>
    /// Used for distributing a document-level discount or a landed cost across
    /// lines. The largest-remainder method guarantees the parts reconcile to the
    /// whole, which naive per-line rounding does not.
    /// </remarks>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        if (weights.Count == 0)
        {
            return [];
        }

        if (weights.Any(w => w < 0m))
        {
            throw new ArgumentException("Weights must be non-negative.", nameof(weights));
        }

        decimal totalWeight = weights.Sum();

        if (totalWeight <= 0m)
        {
            throw new ArgumentException("Weights must sum to more than zero.", nameof(weights));
        }

        string currency = Currency;
        decimal target = decimal.Round(Amount, PresentationScale, PresentationRounding);
        decimal unit = 1m / Pow10(PresentationScale);

        decimal[] exact = [.. weights.Select(w => target * w / totalWeight)];
        decimal[] parts = [.. exact.Select(v => decimal.Truncate(v / unit) * unit)];

        decimal distributed = parts.Sum();
        int remainderUnits = (int)decimal.Round((target - distributed) / unit, 0, MidpointRounding.AwayFromZero);

        int[] order = [.. Enumerable.Range(0, exact.Length)
            .OrderByDescending(i => exact[i] - parts[i])
            .ThenBy(i => i)];

        for (int n = 0; n < Math.Abs(remainderUnits); n++)
        {
            int index = order[n % order.Length];
            parts[index] += remainderUnits > 0 ? unit : -unit;
        }

        return [.. parts.Select(v => new Money(v, currency))];
    }

    /// <summary>Adds two amounts of the same currency.</summary>
    public static Money operator +(Money left, Money right)
        => new(left.Amount + SameCurrency(left, right).Amount, left.Currency);

    /// <summary>Subtracts two amounts of the same currency.</summary>
    public static Money operator -(Money left, Money right)
        => new(left.Amount - SameCurrency(left, right).Amount, left.Currency);

    /// <summary>Negates an amount.</summary>
    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    /// <summary>Multiplies an amount by a scalar, such as a quantity.</summary>
    public static Money operator *(Money left, decimal factor) => new(left.Amount * factor, left.Currency);

    /// <summary>Multiplies a scalar by an amount.</summary>
    public static Money operator *(decimal factor, Money right) => right * factor;

    /// <summary>Divides an amount by a scalar.</summary>
    public static Money operator /(Money left, decimal divisor)
    {
        if (divisor == 0m)
        {
            throw new DivideByZeroException("Cannot divide money by zero.");
        }

        return new Money(left.Amount / divisor, left.Currency);
    }

    /// <summary>Determines whether the left amount is less than the right.</summary>
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether the left amount is greater than the right.</summary>
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether the left amount is less than or equal to the right.</summary>
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether the left amount is greater than or equal to the right.</summary>
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>Adds two amounts of the same currency.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The sum.</returns>
    public static Money Add(Money left, Money right) => left + right;

    /// <summary>Subtracts two amounts of the same currency.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The difference.</returns>
    public static Money Subtract(Money left, Money right) => left - right;

    /// <summary>Multiplies an amount by a scalar.</summary>
    /// <param name="left">The amount.</param>
    /// <param name="factor">The scalar.</param>
    /// <returns>The product.</returns>
    public static Money Multiply(Money left, decimal factor) => left * factor;

    /// <summary>Divides an amount by a scalar.</summary>
    /// <param name="left">The amount.</param>
    /// <param name="divisor">The scalar.</param>
    /// <returns>The quotient.</returns>
    public static Money Divide(Money left, decimal divisor) => left / divisor;

    /// <summary>Negates an amount.</summary>
    /// <param name="value">The amount.</param>
    /// <returns>The negated amount.</returns>
    public static Money Negate(Money value) => -value;

    /// <summary>Sums amounts, all of which must share a currency.</summary>
    /// <param name="values">The amounts.</param>
    /// <param name="currency">Currency used when the sequence is empty.</param>
    /// <returns>The total.</returns>
    public static Money Sum(IEnumerable<Money> values, string currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        Money total = Zero(currency);
        foreach (Money value in values)
        {
            total += value;
        }

        return total;
    }

    /// <inheritdoc />
    public int CompareTo(Money other) => Amount.CompareTo(SameCurrency(this, other).Amount);

    /// <inheritdoc />
    public override string ToString()
        => FormattableString.Invariant($"{Currency} {Amount.ToString("N" + PresentationScale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}");

    private static Money SameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Cannot combine {left.Currency} with {right.Currency}. Convert explicitly first."));
        }

        return right;
    }

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
