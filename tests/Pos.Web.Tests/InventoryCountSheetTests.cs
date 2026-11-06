using FluentAssertions;
using Pos.Web.Components.Pages;
using Pos.Web.Services;

namespace Pos.Web.Tests;

/// <summary>
/// The stock count sheet's working state. The figure under test is the variance
/// an operator reads before deciding to post it against the ledger, so these
/// cover what the sheet says while counts are still drafts.
/// </summary>
public sealed class InventoryCountSheetTests
{
    private static readonly Guid Rice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Sugar = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Salt = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static PosInventoryCountLineView Line(
        int lineNo,
        Guid productId,
        decimal system,
        decimal? physical = null,
        decimal unitCost = 10m,
        bool repeat = false)
        => new(
            lineNo,
            productId,
            BatchId: null,
            SystemQuantity: system,
            PhysicalQuantity: physical,
            Variance: physical is { } counted ? counted - system : null,
            UnitCost: unitCost,
            VarianceValue: physical is { } value ? (value - system) * unitCost : null,
            IsRepeatVariance: repeat,
            CountedAtUtc: physical is null ? null : DateTimeOffset.UnixEpoch);

    private static (string Sku, string Name) Describe(Guid productId)
        => productId == Rice ? ("SKU-RICE", "Jasmine Rice 5kg")
            : productId == Sugar ? ("SKU-SUGR", "Brown Sugar 1kg")
            : ("SKU-SALT", "Iodised Salt 500g");

    private static InventoryCountSheet Sheet(params PosInventoryCountLineView[] lines)
    {
        InventoryCountSheet sheet = new();
        sheet.Load(lines);
        return sheet;
    }

    [Fact]
    public void AFreshSheet_TakesItsQuantitiesFromTheServer()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m, physical: 8m), Line(2, Sugar, system: 4m));

        sheet.Draft(1).Should().Be(8m);
        sheet.Draft(2).Should().BeNull();
        sheet.HasUnsavedChanges.Should().BeFalse();
        sheet.CountedLines.Should().Be(1);
        sheet.RemainingLines.Should().Be(1);
    }

    [Fact]
    public void ATypedQuantity_ShowsItsVarianceBeforeItIsSaved()
    {
        // The whole point of the change: the operator types 5 against a system
        // quantity of 10 and sees -5 at once, not after a round trip.
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m, unitCost: 25m));

        sheet.SetDraft(1, "5").Should().BeNull();

        PosInventoryCountLineView line = sheet.Lines[0];
        sheet.Variance(line).Should().Be(-5m);
        sheet.Value(line).Should().Be(-125m);
        sheet.IsDirty(1).Should().BeTrue();
        sheet.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void AnUncountedLine_HasNoVarianceRatherThanAVarianceOfMinusEverything()
    {
        // Treating "not counted yet" as zero would read as the whole bucket
        // being missing, and would value the shrinkage of an untouched shelf.
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 12m));

        sheet.Variance(sheet.Lines[0]).Should().BeNull();
        sheet.Value(sheet.Lines[0]).Should().BeNull();
        sheet.VaryingLines.Should().Be(0);
        sheet.NetVarianceValue.Should().Be(0m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void AnUnreadableQuantity_PutsAnUncountedLineBackAndSaysWhy(string typed)
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m));

        string? refusal = sheet.SetDraft(1, typed);

        refusal.Should().Be("Enter a non-negative quantity to count this line.");
        sheet.Draft(1).Should().BeNull();
    }

    [Fact]
    public void ClearingASavedCount_IsRefusedAndTheSavedQuantityStands()
    {
        // Blanking a counted line is not the same as finding none of it: one
        // leaves the sheet incomplete, the other posts a write-off.
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m, physical: 7m));

        string? refusal = sheet.SetDraft(1, string.Empty);

        refusal.Should().Be("A saved count cannot be cleared. Enter zero if none of this product was found.");
        sheet.Draft(1).Should().Be(7m);
        sheet.IsDirty(1).Should().BeFalse();
    }

    [Fact]
    public void CountingNoneOfAProduct_IsAcceptedAsZero()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m, unitCost: 3m));

        sheet.SetDraft(1, "0").Should().BeNull();

        sheet.Draft(1).Should().Be(0m);
        sheet.Variance(sheet.Lines[0]).Should().Be(-10m);
        sheet.Value(sheet.Lines[0]).Should().Be(-30m);
        sheet.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void ANegativeQuantity_IsFloorededAtZeroBecauseAShelfCannotHoldLessThanNone()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m));

        sheet.SetDraft(1, "-4");

        sheet.Draft(1).Should().Be(0m);
    }

    [Fact]
    public void AQuantity_IsKeptToThreeDecimals()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m));

        sheet.SetDraft(1, "2.98765");

        sheet.Draft(1).Should().Be(2.988m);
    }

    [Fact]
    public void EachScan_AddsOneToThatLine()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m));

        sheet.Increment(1).Should().Be(1m);
        sheet.Increment(1).Should().Be(2m);
        sheet.Increment(1, 3m).Should().Be(5m);
        sheet.Draft(1).Should().Be(5m);
    }

    [Fact]
    public void ASheetWithALineLeft_CannotBeSubmitted()
    {
        // The server refuses a partial submission and names the uncounted
        // lines; the page asks the same question rather than spend the trip.
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m, physical: 10m), Line(2, Sugar, system: 4m));

        sheet.IsComplete.Should().BeFalse();
        sheet.HasAnyCount.Should().BeTrue();

        sheet.SetDraft(2, "4");

        sheet.IsComplete.Should().BeTrue();
        sheet.RemainingLines.Should().Be(0);
    }

    [Fact]
    public void AnEmptySheet_IsNotComplete()
    {
        InventoryCountSheet sheet = Sheet();

        sheet.IsComplete.Should().BeFalse();
        sheet.TotalLines.Should().Be(0);
    }

    [Fact]
    public void TheTotals_NetOutAndAlsoReportTheAbsoluteFigureApprovalIsMeasuredAgainst()
    {
        InventoryCountSheet sheet = Sheet(
            Line(1, Rice, system: 10m, unitCost: 20m),
            Line(2, Sugar, system: 10m, unitCost: 20m),
            Line(3, Salt, system: 5m, unitCost: 20m));

        sheet.SetDraft(1, "12");   // +2 → +40
        sheet.SetDraft(2, "8");    // -2 → -40
        sheet.SetDraft(3, "5");    //  0 →   0

        sheet.NetVarianceValue.Should().Be(0m);
        sheet.AbsoluteVarianceValue.Should().Be(80m);
        sheet.VaryingLines.Should().Be(2);
    }

    [Fact]
    public void OnlyCountedLines_AreSentToTheServer()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m), Line(2, Sugar, system: 4m, physical: 4m));

        sheet.SetDraft(1, "9");

        List<PosCountLineRequest> requests = sheet.CountedRequests();

        requests.Should().HaveCount(2);
        requests.Should().ContainSingle(r => r.ProductId == Rice && r.PhysicalQuantity == 9m);
        requests.Should().ContainSingle(r => r.ProductId == Sugar && r.PhysicalQuantity == 4m);
    }

    [Fact]
    public void AnUncountedLine_IsLeftOutOfWhatIsSent()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m), Line(2, Sugar, system: 4m));

        sheet.CountedRequests().Should().BeEmpty();
    }

    [Fact]
    public void TheToCountFilter_ShowsWhatIsLeft()
    {
        InventoryCountSheet sheet = Sheet(
            Line(1, Rice, system: 10m, physical: 10m),
            Line(2, Sugar, system: 4m),
            Line(3, Salt, system: 2m));

        sheet.Visible(CountSheetFilter.Uncounted, null, Describe)
            .Select(line => line.LineNo).Should().Equal(2, 3);
        sheet.CountFor(CountSheetFilter.Uncounted).Should().Be(2);
    }

    [Fact]
    public void TheToCountFilter_DropsALineAsSoonAsItIsCounted()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m), Line(2, Sugar, system: 4m));

        sheet.SetDraft(1, "10");

        sheet.Visible(CountSheetFilter.Uncounted, null, Describe)
            .Select(line => line.LineNo).Should().Equal(2);
    }

    [Fact]
    public void TheVaryingFilter_FollowsTheDraftsRatherThanWhatWasSaved()
    {
        InventoryCountSheet sheet = Sheet(
            Line(1, Rice, system: 10m, physical: 10m),
            Line(2, Sugar, system: 4m, physical: 4m));

        sheet.Visible(CountSheetFilter.Variances, null, Describe).Should().BeEmpty();

        sheet.SetDraft(2, "1");

        sheet.Visible(CountSheetFilter.Variances, null, Describe)
            .Select(line => line.LineNo).Should().Equal(2);
        sheet.CountFor(CountSheetFilter.Variances).Should().Be(1);
    }

    [Fact]
    public void TheRepeatsFilter_ShowsOnlyFlaggedLinesThatActuallyVary()
    {
        // A repeat flag on a line that now balances is history, not a finding.
        InventoryCountSheet sheet = Sheet(
            Line(1, Rice, system: 10m, physical: 9m, repeat: true),
            Line(2, Sugar, system: 4m, physical: 4m, repeat: true),
            Line(3, Salt, system: 2m, physical: 1m));

        sheet.Visible(CountSheetFilter.Repeats, null, Describe)
            .Select(line => line.LineNo).Should().Equal(1);
    }

    [Theory]
    [InlineData("jasmine", 1)]
    [InlineData("RICE", 1)]
    [InlineData("sku-sugr", 2)]
    [InlineData("kg", 2)]
    public void Searching_MatchesNameAndSkuWithoutRegardToCase(string term, int expectedLineNo)
    {
        InventoryCountSheet sheet = Sheet(
            Line(1, Rice, system: 10m),
            Line(2, Sugar, system: 4m),
            Line(3, Salt, system: 2m));

        IEnumerable<PosInventoryCountLineView> found = sheet.Visible(CountSheetFilter.All, term, Describe);

        found.Should().Contain(line => line.LineNo == expectedLineNo);
        found.Should().NotContain(line => line.LineNo == 3);
    }

    [Fact]
    public void SearchingAndFiltering_ApplyTogether()
    {
        InventoryCountSheet sheet = Sheet(
            Line(1, Rice, system: 10m),
            Line(2, Sugar, system: 4m, physical: 4m),
            Line(3, Salt, system: 2m));

        sheet.Visible(CountSheetFilter.Uncounted, "kg", Describe)
            .Select(line => line.LineNo).Should().Equal(1);
    }

    [Fact]
    public void AnEmptySearch_LeavesTheFilterAlone()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m), Line(2, Sugar, system: 4m));

        sheet.Visible(CountSheetFilter.All, "   ", Describe).Should().HaveCount(2);
    }

    [Fact]
    public void ReloadingAfterASave_ClearsTheDraftsAgainstTheOldSheet()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m));
        sheet.SetDraft(1, "7");
        sheet.HasUnsavedChanges.Should().BeTrue();

        sheet.Load([Line(1, Rice, system: 10m, physical: 7m)]);

        sheet.HasUnsavedChanges.Should().BeFalse();
        sheet.Draft(1).Should().Be(7m);
        sheet.CountedLines.Should().Be(1);
    }

    [Fact]
    public void ReloadingAShorterSheet_ForgetsTheLinesThatAreGone()
    {
        InventoryCountSheet sheet = Sheet(Line(1, Rice, system: 10m), Line(2, Sugar, system: 4m));
        sheet.SetDraft(2, "4");

        sheet.Load([Line(1, Rice, system: 10m)]);

        sheet.TotalLines.Should().Be(1);
        sheet.CountedLines.Should().Be(0);
        sheet.HasUnsavedChanges.Should().BeFalse();
    }
}
