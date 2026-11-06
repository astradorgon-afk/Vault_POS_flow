using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Domain.Tests.Purchasing;

/// <summary>
/// The warehouse's own restock request. Unlike a store's request there is
/// nowhere to transfer from, so the request is filled by buying — possibly from
/// more than one supplier, which is what the partial-order path is for.
/// </summary>
public sealed class SupplyRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Warehouse = LocationId.New();
    private static readonly ProductId Sugar = ProductId.New();
    private static readonly ProductId Soy = ProductId.New();
    private static readonly UserId Storeman = UserId.New();
    private static readonly UserId Owner = UserId.New();

    private static SupplyRequest Raised(params SupplyRequestLineSpec[] specs)
    {
        Result<SupplyRequest> result = SupplyRequest.Create(
            Warehouse,
            specs.Length == 0 ? [new SupplyRequestLineSpec(Sugar, 40m)] : specs,
            "Running low before the weekend.",
            Storeman,
            Now);

        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    [Fact]
    public void Create_WithProducts_WaitsOnTheOwnerWithNothingOrdered()
    {
        SupplyRequest request = Raised(
            new SupplyRequestLineSpec(Sugar, 40m),
            new SupplyRequestLineSpec(Soy, 12m, "The 1L bottles."));

        request.Status.Should().Be(SupplyRequestStatus.Submitted);
        request.IsOpen.Should().BeTrue();
        request.Lines.Should().HaveCount(2);
        request.Lines.Select(l => l.LineNo).Should().Equal(1, 2);
        request.Lines.Should().OnlyContain(l => l.OrderedQuantity == 0m);
        request.Lines[1].OutstandingQuantity.Should().Be(12m);
        request.Orders.Should().BeEmpty();
    }

    [Fact]
    public void Create_WithoutProducts_IsRefused()
    {
        Result<SupplyRequest> result = SupplyRequest.Create(Warehouse, [], null, Storeman, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.supply_request_empty");
    }

    [Fact]
    public void Create_WithTheSameProductTwice_IsRefused()
    {
        Result<SupplyRequest> result = SupplyRequest.Create(
            Warehouse,
            [new SupplyRequestLineSpec(Sugar, 40m), new SupplyRequestLineSpec(Sugar, 10m)],
            null,
            Storeman,
            Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.supply_request_duplicate_line");
    }

    [Fact]
    public void Create_WithAQuantityOfZero_IsRefused()
    {
        Result<SupplyRequest> result = SupplyRequest.Create(
            Warehouse, [new SupplyRequestLineSpec(Sugar, 0m)], null, Storeman, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.supply_request_quantity_invalid");
    }

    [Fact]
    public void RecordPurchaseOrder_CoveringEveryLine_ClosesTheRequest()
    {
        SupplyRequest request = Raised(
            new SupplyRequestLineSpec(Sugar, 40m),
            new SupplyRequestLineSpec(Soy, 12m));
        PurchaseOrderId order = PurchaseOrderId.New();

        Result recorded = request.RecordPurchaseOrder(
            order,
            new Dictionary<int, decimal> { [1] = 40m, [2] = 12m },
            Owner,
            Now.AddHours(2));

        recorded.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Ordered);
        request.IsOpen.Should().BeFalse();
        request.Lines.Should().OnlyContain(l => l.OutstandingQuantity == 0m);
        request.Orders.Should().ContainSingle(o => o.PurchaseOrderId == order);
        request.DecidedByUserId.Should().Be(Owner);
    }

    [Fact]
    public void RecordPurchaseOrder_CoveringOneLine_LeavesTheRestToBuy()
    {
        SupplyRequest request = Raised(
            new SupplyRequestLineSpec(Sugar, 40m),
            new SupplyRequestLineSpec(Soy, 12m));

        Result recorded = request.RecordPurchaseOrder(
            PurchaseOrderId.New(),
            new Dictionary<int, decimal> { [1] = 40m },
            Owner,
            Now.AddHours(2));

        recorded.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.PartiallyOrdered);
        request.IsOpen.Should().BeTrue();
        request.Lines[0].OutstandingQuantity.Should().Be(0m);
        request.Lines[1].OutstandingQuantity.Should().Be(12m);
    }

    [Fact]
    public void RecordPurchaseOrder_TwiceFromDifferentSuppliers_FillsTheRequest()
    {
        SupplyRequest request = Raised(
            new SupplyRequestLineSpec(Sugar, 40m),
            new SupplyRequestLineSpec(Soy, 12m));

        request.RecordPurchaseOrder(
            PurchaseOrderId.New(), new Dictionary<int, decimal> { [1] = 40m }, Owner, Now.AddHours(2));
        Result second = request.RecordPurchaseOrder(
            PurchaseOrderId.New(), new Dictionary<int, decimal> { [2] = 12m }, Owner, Now.AddHours(3));

        second.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Ordered);
        request.Orders.Should().HaveCount(2);
    }

    [Fact]
    public void RecordPurchaseOrder_ForALineNotOnTheRequest_IsRefused()
    {
        SupplyRequest request = Raised();

        Result recorded = request.RecordPurchaseOrder(
            PurchaseOrderId.New(), new Dictionary<int, decimal> { [9] = 5m }, Owner, Now);

        recorded.IsFailure.Should().BeTrue();
        recorded.Errors.Should().Contain(e => e.Code == "purchasing.supply_request_line_unknown");
        request.Status.Should().Be(SupplyRequestStatus.Submitted);
    }

    [Fact]
    public void RecordPurchaseOrder_RoundingUpToACasePrice_IsAllowed()
    {
        SupplyRequest request = Raised(new SupplyRequestLineSpec(Sugar, 40m));

        Result recorded = request.RecordPurchaseOrder(
            PurchaseOrderId.New(), new Dictionary<int, decimal> { [1] = 48m }, Owner, Now);

        recorded.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Ordered);
    }

    [Fact]
    public void Reject_WithAReason_ClosesTheRequest()
    {
        SupplyRequest request = Raised();

        Result rejected = request.Reject("Cash is tight until the end of the month.", Owner, Now.AddHours(1));

        rejected.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Rejected);
        request.DecisionReason.Should().StartWith("Cash is tight");
        request.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Reject_WithoutAReason_IsRefused()
    {
        SupplyRequest request = Raised();

        Result rejected = request.Reject("  ", Owner, Now);

        rejected.IsFailure.Should().BeTrue();
        rejected.Errors.Should().Contain(e => e.Code == "purchasing.supply_request_reason_required");
        request.Status.Should().Be(SupplyRequestStatus.Submitted);
    }

    [Fact]
    public void Reject_AfterSomethingWasOrdered_IsRefused()
    {
        SupplyRequest request = Raised(
            new SupplyRequestLineSpec(Sugar, 40m),
            new SupplyRequestLineSpec(Soy, 12m));
        request.RecordPurchaseOrder(
            PurchaseOrderId.New(), new Dictionary<int, decimal> { [1] = 40m }, Owner, Now.AddHours(1));

        Result rejected = request.Reject("Changed my mind about the rest.", Owner, Now.AddHours(2));

        rejected.IsFailure.Should().BeTrue();
        rejected.Errors.Should().Contain(e => e.Code == "purchasing.supply_request_not_open");
        request.Status.Should().Be(SupplyRequestStatus.PartiallyOrdered);
    }

    [Fact]
    public void Cancel_BeforeAnyOrder_WithdrawsTheRequest()
    {
        SupplyRequest request = Raised();

        Result cancelled = request.Cancel("Counted again and we have plenty.", Storeman, Now.AddMinutes(10));

        cancelled.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Cancelled);
    }

    [Fact]
    public void ReleasePurchaseOrder_AfterTheOnlyOrderDies_ReopensTheRequest()
    {
        SupplyRequest request = Raised(
            new SupplyRequestLineSpec(Sugar, 40m),
            new SupplyRequestLineSpec(Soy, 12m));
        PurchaseOrderId order = PurchaseOrderId.New();
        request.RecordPurchaseOrder(
            order, new Dictionary<int, decimal> { [1] = 40m, [2] = 12m }, Owner, Now.AddHours(1));

        Result released = request.ReleasePurchaseOrder(order, Now.AddHours(4));

        released.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Submitted);
        request.IsOpen.Should().BeTrue();
        request.Lines[0].OutstandingQuantity.Should().Be(40m);
        request.Lines[1].OutstandingQuantity.Should().Be(12m);
        request.Orders.Should().ContainSingle(o => o.PurchaseOrderId == order && o.IsReleased);
    }

    [Fact]
    public void ReleasePurchaseOrder_WhenAnotherOrderStands_LeavesThatOnesClaimAlone()
    {
        SupplyRequest request = Raised(
            new SupplyRequestLineSpec(Sugar, 40m),
            new SupplyRequestLineSpec(Soy, 12m));
        PurchaseOrderId first = PurchaseOrderId.New();
        PurchaseOrderId second = PurchaseOrderId.New();
        request.RecordPurchaseOrder(first, new Dictionary<int, decimal> { [1] = 40m }, Owner, Now.AddHours(1));
        request.RecordPurchaseOrder(second, new Dictionary<int, decimal> { [2] = 12m }, Owner, Now.AddHours(2));

        request.ReleasePurchaseOrder(second, Now.AddHours(5));

        request.Status.Should().Be(SupplyRequestStatus.PartiallyOrdered);
        request.Lines[0].OutstandingQuantity.Should().Be(0m);
        request.Lines[1].OutstandingQuantity.Should().Be(12m);
    }

    [Fact]
    public void ReleasePurchaseOrder_Twice_ChangesNothingTheSecondTime()
    {
        SupplyRequest request = Raised(new SupplyRequestLineSpec(Sugar, 40m));
        PurchaseOrderId order = PurchaseOrderId.New();
        request.RecordPurchaseOrder(order, new Dictionary<int, decimal> { [1] = 40m }, Owner, Now.AddHours(1));

        request.ReleasePurchaseOrder(order, Now.AddHours(4));
        Result again = request.ReleasePurchaseOrder(order, Now.AddHours(6));

        again.IsSuccess.Should().BeTrue();
        request.Lines[0].OutstandingQuantity.Should().Be(40m);
        request.Status.Should().Be(SupplyRequestStatus.Submitted);
    }

    [Fact]
    public void ReleasePurchaseOrder_ForAnOrderThisRequestNeverHeld_IsHarmless()
    {
        SupplyRequest request = Raised(new SupplyRequestLineSpec(Sugar, 40m));
        request.RecordPurchaseOrder(
            PurchaseOrderId.New(), new Dictionary<int, decimal> { [1] = 40m }, Owner, Now.AddHours(1));

        Result released = request.ReleasePurchaseOrder(PurchaseOrderId.New(), Now.AddHours(4));

        released.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Ordered);
        request.Lines[0].OutstandingQuantity.Should().Be(0m);
    }

    [Fact]
    public void ReleasePurchaseOrder_ThenOrderingAgain_ClosesTheRequest()
    {
        SupplyRequest request = Raised(new SupplyRequestLineSpec(Sugar, 40m));
        PurchaseOrderId wrongSupplier = PurchaseOrderId.New();
        request.RecordPurchaseOrder(
            wrongSupplier, new Dictionary<int, decimal> { [1] = 40m }, Owner, Now.AddHours(1));
        request.ReleasePurchaseOrder(wrongSupplier, Now.AddHours(2));

        Result retry = request.RecordPurchaseOrder(
            PurchaseOrderId.New(), new Dictionary<int, decimal> { [1] = 40m }, Owner, Now.AddHours(3));

        retry.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(SupplyRequestStatus.Ordered);
        request.Orders.Should().HaveCount(2);
    }

    [Fact]
    public void AssignNumber_OnlyTakesTheFirstOne()
    {
        SupplyRequest request = Raised();

        request.AssignNumber("SRQ-2026-000001");
        request.AssignNumber("SRQ-2026-000002");

        request.Number.Should().Be("SRQ-2026-000001");
    }
}
