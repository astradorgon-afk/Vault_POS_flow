using FluentAssertions;
using Pos.Domain.Common;

namespace Pos.Domain.Tests.Common;

/// <summary>
/// Money arithmetic. Retail totals must reconcile exactly; a rounding rule that
/// drifts by a centavo per line becomes a visible cash variance by closing time.
/// </summary>
public sealed class MoneyTests
{
    private const string Php = "PHP";

    [Fact]
    public void Amount_IsHeldAtStorageScale()
    {
        Money value = new(12.34567m, Php);
        value.Amount.Should().Be(12.3457m);
    }

    [Fact]
    public void Currency_IsNormalisedToUpperCase()
    {
        new Money(1m, "php").Currency.Should().Be("PHP");
    }

    [Theory]
    [InlineData("PH")]
    [InlineData("PHPX")]
    [InlineData("12P")]
    public void InvalidCurrency_IsRejected(string currency)
    {
        Action act = () => _ = new Money(1m, currency);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MixingCurrencies_IsRefused()
    {
        Money php = new(100m, Php);
        Money usd = new(100m, "USD");

        Action act = () => _ = php + usd;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PHP*USD*");
    }

    [Fact]
    public void Presentation_RoundsHalfAwayFromZero()
    {
        new Money(10.125m, Php).ToPresentation().Amount.Should().Be(10.13m);
        new Money(-10.125m, Php).ToPresentation().Amount.Should().Be(-10.13m);
        new Money(10.124m, Php).ToPresentation().Amount.Should().Be(10.12m);
    }

    [Fact]
    public void CashRounding_SnapsToTheConfiguredIncrement()
    {
        new Money(103.47m, Php).RoundToCashIncrement(0.05m).Amount.Should().Be(103.45m);
        new Money(103.48m, Php).RoundToCashIncrement(0.05m).Amount.Should().Be(103.50m);
        new Money(103.47m, Php).RoundToCashIncrement(1m).Amount.Should().Be(103m);
    }

    [Fact]
    public void Allocate_DistributesEveryCentele_WithoutLossOrInvention()
    {
        // 100.00 split three ways is the canonical rounding trap: 33.33 x 3
        // leaves a centavo behind unless the remainder is assigned.
        Money total = new(100m, Php);

        IReadOnlyList<Money> parts = total.Allocate([1m, 1m, 1m]);

        parts.Should().HaveCount(3);
        parts.Sum(p => p.Amount).Should().Be(100m);
        parts.Select(p => p.Amount).Should().BeEquivalentTo(new[] { 33.34m, 33.33m, 33.33m });
    }

    [Fact]
    public void Allocate_RespectsWeights_AndStillReconciles()
    {
        Money discount = new(10m, Php);

        IReadOnlyList<Money> parts = discount.Allocate([70m, 20m, 10m]);

        parts.Sum(p => p.Amount).Should().Be(10m);
        parts[0].Amount.Should().Be(7.00m);
        parts[1].Amount.Should().Be(2.00m);
        parts[2].Amount.Should().Be(1.00m);
    }

    [Fact]
    public void Allocate_HandlesNegativeAmounts()
    {
        Money refund = new(-100m, Php);

        IReadOnlyList<Money> parts = refund.Allocate([1m, 1m, 1m]);

        parts.Sum(p => p.Amount).Should().Be(-100m);
    }

    [Fact]
    public void Sum_OfEmptySequence_IsZeroInTheGivenCurrency()
    {
        Money total = Money.Sum([], Php);
        total.IsZero.Should().BeTrue();
        total.Currency.Should().Be(Php);
    }

    [Fact]
    public void Comparison_OrdersByAmount()
    {
        Money low = new(10m, Php);
        Money high = new(20m, Php);

        (low < high).Should().BeTrue();
        (high >= low).Should().BeTrue();
        low.CompareTo(high).Should().BeNegative();
    }

    [Fact]
    public void DivideByZero_Throws()
    {
        Action act = () => _ = new Money(10m, Php) / 0m;
        act.Should().Throw<DivideByZeroException>();
    }

    [Fact]
    public void LineTotal_ComputedFromDecimals_IsExact()
    {
        // The float trap: 0.1 + 0.2 != 0.3 in binary floating point.
        Money a = new(0.1m, Php);
        Money b = new(0.2m, Php);

        (a + b).Amount.Should().Be(0.3m);
    }
}
