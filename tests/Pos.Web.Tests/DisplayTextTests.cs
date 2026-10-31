using FluentAssertions;
using Pos.Web.Services;

namespace Pos.Web.Tests;

/// <summary>Quantities stored with the ledger's four decimals read cleanly on screen.</summary>
public sealed class DisplayTextTests
{
    [Theory]
    [InlineData("0.0000 units available; minimum 6.0000, reorder point 16.0000.", "0 units available; minimum 6, reorder point 16.")]
    [InlineData("Lot LOT-42 has 12.5000 units available and expires on 2026-09-24.", "Lot LOT-42 has 12.5 units available and expires on 2026-09-24.")]
    [InlineData("1,234.5000 units available", "1,234.5 units available")]
    public void LedgerDecimals_LoseTheirTrailingZeros(string stored, string shown)
        => DisplayText.TidyQuantities(stored).Should().Be(shown);

    [Theory]
    [InlineData("Price is 12.50 today")]
    [InlineData("Version 1.2.3456 shipped")]
    [InlineData("Order PO-2026-000009 is ready")]
    [InlineData("")]
    public void OtherNumbers_AreLeftAlone(string text)
        => DisplayText.TidyQuantities(text).Should().Be(text);
}
