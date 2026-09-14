using FluentAssertions;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>The Philippine VAT arithmetic for sale lines (POS.md §2.6).</summary>
public sealed class SalesVatTests
{
    [Theory]
    [InlineData(0.12)]
    [InlineData(0.15)]
    [InlineData(0.20)]
    public void SplitTaxInclusive_RecoversBaseAndVat_ThatMatchTheGross(decimal rate)
    {
        const decimal gross = 100m;

        (decimal vatBase, decimal vat) = SalesVat.SplitTaxInclusive(gross, rate);

        decimal vatBaseRounded = decimal.Round(gross / (1m + rate), 4, MidpointRounding.ToEven);
        decimal vatRounded = decimal.Round(gross - vatBaseRounded, 4, MidpointRounding.ToEven);

        vatBase.Should().Be(vatBaseRounded);
        vat.Should().Be(vatRounded);
        (vatBase + vat).Should().Be(gross);
    }

    [Fact]
    public void SplitTaxInclusive_TwelvePercentOfOneHundred_SplitsAtFourDp()
    {
        // The canonical Philippine case: 100.00 pesos including 12% VAT.
        (decimal vatBase, decimal vat) = SalesVat.SplitTaxInclusive(100m, 0.12m);

        vatBase.Should().Be(89.2857m);
        vat.Should().Be(10.7143m);
    }

    [Fact]
    public void SplitTaxInclusive_ZeroRate_IsRejected()
    {
        Action split = () => SalesVat.SplitTaxInclusive(100m, 0m);

        split.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SplitTaxInclusive_NegativeCharge_IsRejected()
    {
        Action split = () => SalesVat.SplitTaxInclusive(-1m, 0.12m);

        split.Should().Throw<ArgumentOutOfRangeException>();
    }
}