using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Tests.Inventory;

/// <summary>
/// The inventory count's rules: each kind needs its scope, the variance is always
/// derived, a scoped count accepts only what is on its sheet, and a count is
/// submitted only when every line is counted.
/// </summary>
public sealed class InventoryCountTests
{
    private static readonly LocationId Store = LocationId.New();
    private static readonly UserId Counter = UserId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly ProductId Rice = ProductId.New();
    private static readonly ProductId Oil = ProductId.New();

    [Theory]
    [InlineData(InventoryCountKind.FullPhysical, false, false, true)]
    [InlineData(InventoryCountKind.FullPhysical, true, false, false)]
    [InlineData(InventoryCountKind.Category, true, false, true)]
    [InlineData(InventoryCountKind.Category, false, true, false)]
    [InlineData(InventoryCountKind.ProductSpecific, false, true, true)]
    [InlineData(InventoryCountKind.ProductSpecific, false, false, false)]
    [InlineData(InventoryCountKind.Cycle, true, true, true)]
    [InlineData(InventoryCountKind.Cycle, false, false, false)]
    public void EachKind_NeedsItsScope(InventoryCountKind kind, bool categories, bool products, bool valid)
        => InventoryCount.ValidateScope(kind, categories, products).IsSuccess.Should().Be(valid);

    [Fact]
    public void Variance_IsCountedMinusSystem_ValuedAtTheLineCost()
    {
        InventoryCount count = Open(InventoryCountKind.ProductSpecific, new InventoryCountSheetItem(Rice, null, 105m, 40m));

        count.RecordCount(Rice, null, false, 101m, 105m, 40m, Counter, Now).IsSuccess.Should().BeTrue();

        InventoryCountLine line = count.Lines.Single();
        line.Variance.Should().Be(-4m);
        line.VarianceValue.Should().Be(-160m);
        count.TotalAbsoluteVarianceValue.Should().Be(160m);
    }

    [Fact]
    public void RecordingAgain_RefreshesTheSystemQuantityAndTheCount()
    {
        InventoryCount count = Open(InventoryCountKind.ProductSpecific, new InventoryCountSheetItem(Rice, null, 10m, 40m));

        count.RecordCount(Rice, null, false, 9m, 10m, 40m, Counter, Now);
        count.RecordCount(Rice, null, false, 7m, 8m, 40m, Counter, Now.AddMinutes(20));

        InventoryCountLine line = count.Lines.Should().ContainSingle().Subject;
        line.SystemQuantity.Should().Be(8m, "two were sold between the counts");
        line.Variance.Should().Be(-1m);
        line.CountedAtUtc.Should().Be(Now.AddMinutes(20));
    }

    [Fact]
    public void ScopedCount_RefusesProductsNotOnItsSheet_ButAFullCountAddsThem()
    {
        InventoryCount scoped = Open(InventoryCountKind.ProductSpecific, new InventoryCountSheetItem(Rice, null, 1m, 40m));
        scoped.RecordCount(Oil, null, false, 3m, 0m, 90m, Counter, Now)
            .Errors.Should().ContainSingle(e => e.Code == "count.product_not_on_sheet");

        InventoryCount full = Open(InventoryCountKind.FullPhysical, new InventoryCountSheetItem(Rice, null, 1m, 40m));
        full.RecordCount(Oil, null, false, 3m, 0m, 90m, Counter, Now).IsSuccess.Should().BeTrue();
        full.Lines.Should().HaveCount(2);
        full.Lines[1].Variance.Should().Be(3m, "stock found that the system did not know about");
    }

    [Fact]
    public void BatchTrackedProduct_ConfirmedEmptyWithoutABatch_ButFoundStockNeedsOne()
    {
        InventoryCount count = Open(InventoryCountKind.ProductSpecific, new InventoryCountSheetItem(Rice, null, 0m, 40m));

        count.RecordCount(Rice, null, true, 0m, 0m, 40m, Counter, Now).IsSuccess.Should().BeTrue();
        count.RecordCount(Rice, null, true, 2m, 0m, 40m, Counter, Now)
            .Errors.Should().ContainSingle(e => e.Code == "inventory_control.batch_required");
        count.RecordCount(Rice, BatchId.New(), true, 2m, 0m, 40m, Counter, Now).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Submit_RequiresEveryLineCounted_AndFlagsRepeatVariance()
    {
        InventoryCount count = Open(
            InventoryCountKind.ProductSpecific,
            new InventoryCountSheetItem(Rice, null, 10m, 40m),
            new InventoryCountSheetItem(Oil, null, 5m, 90m));

        count.RecordCount(Rice, null, false, 8m, 10m, 40m, Counter, Now);
        count.Submit(Counter, new HashSet<ProductId>(), Now)
            .Errors.Should().ContainSingle(e => e.Code == "count.incomplete");

        count.RecordCount(Oil, null, false, 5m, 5m, 90m, Counter, Now);
        count.Submit(Counter, new HashSet<ProductId> { Rice, Oil }, Now).IsSuccess.Should().BeTrue();

        count.Status.Should().Be(InventoryCountStatus.PendingApproval);
        count.Lines.Single(l => l.ProductId == Rice).IsRepeatVariance.Should().BeTrue();
        count.Lines.Single(l => l.ProductId == Oil).IsRepeatVariance.Should().BeFalse("it did not vary this time");
    }

    [Fact]
    public void Rejection_ReturnsTheCountForRecounting_AndCancellationEndsIt()
    {
        InventoryCount count = Open(InventoryCountKind.ProductSpecific, new InventoryCountSheetItem(Rice, null, 10m, 40m));
        count.RecordCount(Rice, null, false, 8m, 10m, 40m, Counter, Now);
        count.Submit(Counter, new HashSet<ProductId>(), Now);

        count.Reject("Recount aisle 4").IsSuccess.Should().BeTrue();
        count.Status.Should().Be(InventoryCountStatus.Counting);
        count.RecordCount(Rice, null, false, 10m, 10m, 40m, Counter, Now).IsSuccess.Should().BeTrue();

        count.Cancel("no").Errors.Should().ContainSingle(e => e.Code == "inventory_control.reason_required");
        count.Cancel("Store closed for renovation").IsSuccess.Should().BeTrue();
        count.RecordCount(Rice, null, false, 1m, 10m, 40m, Counter, Now)
            .Errors.Should().ContainSingle(e => e.Code == "count.invalid_state");
    }

    private static InventoryCount Open(InventoryCountKind kind, params InventoryCountSheetItem[] sheet)
        => InventoryCount.Open(
            DocumentNumber.Create(DocumentType.InventoryCount, 2026, 1),
            Store,
            kind,
            hasCategories: kind is InventoryCountKind.Category or InventoryCountKind.Cycle,
            hasProducts: kind is InventoryCountKind.ProductSpecific or InventoryCountKind.Cycle,
            sheet,
            note: null,
            Counter,
            Now).Value;
}
