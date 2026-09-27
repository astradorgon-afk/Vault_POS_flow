using FluentAssertions;
using Pos.Shared.Scanning;

namespace Pos.Shared.Tests.Scanning;

/// <summary>Cleaning scanner output and matching retail code variants.</summary>
public sealed class BarcodeTextTests
{
    [Theory]
    [InlineData("4800016123456\r\n", "4800016123456")]
    [InlineData("  4800016123456\t", "4800016123456")]
    [InlineData("]E04800016123456", "4800016123456")]
    [InlineData("]C1ABC-123", "ABC-123")]
    [InlineData("\u0002ABC\u0003", "ABC")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Clean_RemovesWhatScannersAdd(string? raw, string expected)
        => BarcodeText.Clean(raw).Should().Be(expected);

    [Fact]
    public void Clean_KeepsTheGs1SeparatorInsideACode()
        => BarcodeText.Clean("0104800016\u001d10ABC").Should().Be("0104800016\u001d10ABC");

    [Fact]
    public void Clean_CapsRunawayInput()
        => BarcodeText.Clean(new string('7', 500)).Length.Should().Be(BarcodeText.MaximumLength);

    [Fact]
    public void UpcA_AlsoMatchesItsEan13Form()
        => BarcodeText.LookupVariants("036000291452").Should().Equal("036000291452", "0036000291452");

    [Fact]
    public void Ean13WithLeadingZero_AlsoMatchesItsUpcAForm()
        => BarcodeText.LookupVariants("0036000291452").Should().Equal("0036000291452", "036000291452");

    [Fact]
    public void OtherCodes_MatchOnlyThemselves()
        => BarcodeText.LookupVariants("SKU-ABC").Should().Equal("SKU-ABC");

    [Theory]
    [InlineData("4006381333931", true)]
    [InlineData("036000291452", true)]
    [InlineData("96385074", true)]
    [InlineData("4006381333932", false)]
    [InlineData("SKU-ABC", false)]
    [InlineData("12345", false)]
    public void IsValidGtin_ChecksTheCheckDigit(string code, bool valid)
        => BarcodeText.IsValidGtin(code).Should().Be(valid);
}
