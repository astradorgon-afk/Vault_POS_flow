using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Domain.Tests.Purchasing;

/// <summary>
/// The purchase order's lifecycle invariants. Money and quantities are
/// rounded at the boundary, decisions are audited as approval rows, and no
/// transition is ever allowed from the wrong state.
/// </summary>
public sealed class PurchaseOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);

    private static readonly SupplierId Supplier = SupplierId.New();
    private static readonly LocationId Store1 = LocationId.New();
    private static readonly UserId Shelf = UserId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly ProductId Rice = ProductId.New();
    private static readonly UnitOfMeasureId Case = UnitOfMeasureId.New();

    [Fact]
    public void Create_ProducesADraftWithNoNumber_AndTotalsFromLines()
    {
        Result<PurchaseOrder> result = PurchaseOrder.Create(
            Supplier,
            Store1,
            [
                Line(Coke, 10m, 100m),
                Line(Rice, 5m, 40m),
            ],
            Shelf,
            Now,
            "PHP",
            expectedAtUtc: Now.AddDays(2));

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        PurchaseOrder order = result.Value;

        order.Id.Should().NotBe(PurchaseOrderId.Empty);
        order.Status.Should().Be(PurchaseOrderStatus.Draft);
        order.Number.Should().BeNull();
        order.SupplierId.Should().Be(Supplier);
        order.DestinationLocationId.Should().Be(Store1);
        order.CreatedByUserId.Should().Be(Shelf);
        order.CreatedAtUtc.Should().Be(Now);
        order.CurrencyCode.Should().Be("PHP");
        order.ExpectedAtUtc.Should().Be(Now.AddDays(2));
        order.Subtotal.Should().Be(1200m);
        order.Lines.Should().HaveCount(2);
        order.Lines.Select(l => l.LineNo).Should().Equal(1, 2);
        order.Lines.Sum(l => l.LineTotal).Should().Be(1200m);
        order.GrandTotal.Should().Be(1200m);
        order.TaxTotal.Should().Be(0m);
    }

    [Fact]
    public void Create_NullCurrency_DefaultsToPhp()
    {
        Result<PurchaseOrder> result = PurchaseOrder.Create(
            Supplier,
            Store1,
            [Line(Coke, 1m, 1m)],
            Shelf,
            Now,
            currencyCode: null,
            expectedAtUtc: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrencyCode.Should().Be("PHP");
    }

    [Fact]
    public void Create_EmptySupplierOrDestination_IsRejected()
    {
        Result<PurchaseOrder> noSupplier = PurchaseOrder.Create(
            SupplierId.Empty,
            Store1,
            [Line(Coke, 1m, 1m)],
            Shelf,
            Now,
            "PHP",
            null);

        noSupplier.IsFailure.Should().BeTrue();
        noSupplier.Error.Code.Should().Be("purchasing.supplier_invalid");

        Result<PurchaseOrder> noDestination = PurchaseOrder.Create(
            Supplier,
            LocationId.Empty,
            [Line(Coke, 1m, 1m)],
            Shelf,
            Now,
            "PHP",
            null);

        noDestination.IsFailure.Should().BeTrue();
        noDestination.Error.Code.Should().Be("purchasing.destination_invalid");
    }

    [Fact]
    public void Create_WithoutLines_IsRejected()
    {
        Result<PurchaseOrder> result = PurchaseOrder.Create(
            Supplier,
            Store1,
            [],
            Shelf,
            Now,
            "PHP",
            null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.empty_order");
    }

    [Fact]
    public void Create_ZeroQuantity_IsRejected()
    {
        Result<PurchaseOrder> result = PurchaseOrder.Create(
            Supplier,
            Store1,
            [Line(Coke, 0m, 10m)],
            Shelf,
            Now,
            "PHP",
            null);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.line_quantity_invalid");
    }

    [Fact]
    public void Create_NegativeUnitCost_IsRejected()
    {
        Result<PurchaseOrder> result = PurchaseOrder.Create(
            Supplier,
            Store1,
            [Line(Coke, 10m, -0.01m)],
            Shelf,
            Now,
            "PHP",
            null);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.line_unit_cost_invalid");
    }

    [Fact]
    public void Create_DuplicateProductLines_AreRejected()
    {
        Result<PurchaseOrder> result = PurchaseOrder.Create(
            Supplier,
            Store1,
            [
                Line(Coke, 10m, 100m),
                Line(Coke, 5m, 80m),
            ],
            Shelf,
            Now,
            "PHP",
            null);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.duplicate_product_line");
    }

    [Fact]
    public void Create_RoundsQuantityToThreePlaces_AndMoneyToFour()
    {
        Result<PurchaseOrder> result = PurchaseOrder.Create(
            Supplier,
            Store1,
            [new PurchaseOrderLineSpec(Coke, Case, 12.34567m, 1.234567m)],
            Shelf,
            Now,
            "PHP",
            null);

        result.IsSuccess.Should().BeTrue();
        PurchaseOrderLine line = result.Value.Lines.Single();

        // Quantity has three decimal places, half away from zero.
        line.OrderedQuantity.Should().Be(12.346m);
        // Cost has four decimal places, half away from zero.
        line.UnitCost.Should().Be(1.2346m);
        // 12.346 x 1.2346 = 15.2423716, rounded to four places.
        line.LineTotal.Should().Be(15.2424m);
        result.Value.GrandTotal.Should().Be(15.2424m);
    }

    [Fact]
    public void Submit_AssignsTheNumber_AndMovesToPendingApproval()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);
        DocumentNumber number = DocumentNumber.Create(DocumentType.PurchaseOrder, 2026, 42);

        Result outcome = order.Submit(number, Now);

        outcome.IsSuccess.Should().BeTrue(because: string.Join("; ", outcome.Errors.Select(e => e.Code)));
        order.Number.Should().Be("PO-2026-000042");
        order.Status.Should().Be(PurchaseOrderStatus.PendingApproval);
    }

    [Fact]
    public void Submit_FromAnyOtherState_IsRejected()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);
        order.Submit(DocumentNumber.Create(DocumentType.PurchaseOrder, 2026, 1), Now);

        Result outcome = order.Submit(DocumentNumber.Create(DocumentType.PurchaseOrder, 2026, 2), Now);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.invalid_state");
    }

    [Fact]
    public void Approve_TransitionsToApproved_AndAuditsTheDecision()
    {
        PurchaseOrder order = PendingApproval();

        Result outcome = order.Approve(Shelf, Now, thresholdApplied: order.GrandTotal, notes: "OK");

        outcome.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.Approved);
        order.Approvals.Should().ContainSingle();
        order.Approvals.Single().Should().Match<PurchaseApproval>(a =>
            a.Decision == PurchaseApprovalDecision.Approve
            && a.ApproverUserId == Shelf
            && a.DecidedAtUtc == Now
            && a.ThresholdApplied == order.GrandTotal
            && a.Notes == "OK");
    }

    [Fact]
    public void Approve_FromAnyOtherState_IsRejected()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);

        Result outcome = order.Approve(Shelf, Now, thresholdApplied: 0m);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.invalid_state");
    }

    [Fact]
    public void Reject_TransitionsToRejected_AndAuditsTheDecision()
    {
        PurchaseOrder order = PendingApproval();

        Result outcome = order.Reject(Shelf, Now, notes: "Price out of band");

        outcome.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.Rejected);
        order.Approvals.Single().Should().Match<PurchaseApproval>(a =>
            a.Decision == PurchaseApprovalDecision.Reject
            && a.ThresholdApplied == order.GrandTotal);
    }

    [Fact]
    public void Send_TransitionsToOrdered_AndStampsTheTimestamp()
    {
        PurchaseOrder order = PendingApproval();
        order.Approve(Shelf, Now, thresholdApplied: order.GrandTotal);

        Result outcome = order.Send(Now.AddHours(1));

        outcome.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.Ordered);
        order.OrderedAtUtc.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Send_BeforeApproval_IsRejected()
    {
        PurchaseOrder order = PendingApproval();

        Result outcome = order.Send(Now);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.invalid_state");
    }

    [Fact]
    public void Cancel_OfADraft_NeedsNoReason()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);

        Result outcome = order.Cancel(Shelf, reason: null, Now);

        outcome.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.Cancelled);
        order.CancelledReason.Should().BeNull();
        order.Approvals.Should().ContainSingle(a => a.Decision == PurchaseApprovalDecision.Cancel);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.PendingApproval)]
    [InlineData(PurchaseOrderStatus.Approved)]
    [InlineData(PurchaseOrderStatus.Ordered)]
    public void Cancel_OfASubmittedOrder_RequiresAReason(PurchaseOrderStatus from)
    {
        PurchaseOrder order = from switch
        {
            PurchaseOrderStatus.PendingApproval => PendingApproval(),
            PurchaseOrderStatus.Approved => Approved(),
            _ => Ordered(),
        };

        Result withoutReason = order.Cancel(Shelf, reason: string.Empty, Now);

        withoutReason.IsFailure.Should().BeTrue();
        withoutReason.Error.Code.Should().Be("purchasing.cancel_reason_required");

        Result withReason = order.Cancel(Shelf, reason: "Buyer changed mind", Now);

        withReason.IsSuccess.Should().BeTrue(because: string.Join("; ", withReason.Errors.Select(e => e.Code)));
        order.Status.Should().Be(PurchaseOrderStatus.Cancelled);
        order.CancelledReason.Should().Be("Buyer changed mind");
    }

    [Fact]
    public void Cancel_OnceFullyReceived_IsRejected()
    {
        PurchaseOrder order = Ordered();
        order.RecordReceipt(new Dictionary<PurchaseOrderLineId, decimal>
        {
            [order.Lines.Single().Id] = order.Lines.Single().OrderedQuantity,
        });

        Result outcome = order.Cancel(Shelf, reason: "Too late", Now);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.invalid_state");
    }

    [Fact]
    public void Withdraw_DeletesTheDraftWithoutANumber()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);

        Result outcome = order.Withdraw(Shelf, Now);

        outcome.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.Cancelled);
        order.Approvals.Should().ContainSingle(a => a.Decision == PurchaseApprovalDecision.Cancel);
    }

    [Fact]
    public void Withdraw_OnceSubmitted_IsRejected()
    {
        PurchaseOrder order = PendingApproval();

        Result outcome = order.Withdraw(Shelf, Now);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.invalid_state");
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Ordered)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.FullyReceived)]
    public void Close_IsAllowedFromReceivedStates(PurchaseOrderStatus from)
    {
        PurchaseOrder order = Ordered();

        if (from != PurchaseOrderStatus.Ordered)
        {
            decimal ordered = order.Lines.Single().OrderedQuantity;
            var received = new Dictionary<PurchaseOrderLineId, decimal>
            {
                [order.Lines.Single().Id] = from == PurchaseOrderStatus.FullyReceived ? ordered : ordered / 2,
            };
            order.RecordReceipt(received);
        }

        order.Status.Should().Be(from, "the setup must reproduce the given state");

        Result outcome = order.Close(Shelf, reason: from == PurchaseOrderStatus.FullyReceived ? null : "Stocktake", Now);

        outcome.IsSuccess.Should().BeTrue(because: string.Join("; ", outcome.Errors.Select(e => e.Code)));
        order.Status.Should().Be(PurchaseOrderStatus.Closed);
        order.Approvals.Should().ContainSingle(a => a.Decision == PurchaseApprovalDecision.Close);
    }

    [Fact]
    public void Close_OfPartiallyReceivedOrder_RequiresAReason()
    {
        PurchaseOrder order = Ordered();
        order.RecordReceipt(new Dictionary<PurchaseOrderLineId, decimal>
        {
            [order.Lines.Single().Id] = order.Lines.Single().OrderedQuantity / 2,
        });

        Result outcome = order.Close(Shelf, reason: null, Now);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.close_reason_required");
    }

    [Fact]
    public void Close_FromADraft_IsRejected()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);

        Result outcome = order.Close(Shelf, reason: "Nope", Now);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.invalid_state");
    }

    [Fact]
    public void RecordReceipt_WhenAllLinesArrive_MovesToFullyReceived()
    {
        PurchaseOrder order = Ordered();

        decimal ordered = order.Lines.Single().OrderedQuantity;
        Result outcome = order.RecordReceipt(new Dictionary<PurchaseOrderLineId, decimal>
        {
            [order.Lines.Single().Id] = ordered,
        });

        outcome.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.FullyReceived);
    }

    [Fact]
    public void RecordReceipt_WhenOnlySomeArrives_MovesToPartiallyReceived()
    {
        PurchaseOrder order = Ordered();

        Result outcome = order.RecordReceipt(new Dictionary<PurchaseOrderLineId, decimal>
        {
            [order.Lines.Single().Id] = order.Lines.Single().OrderedQuantity - 1m,
        });

        outcome.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(PurchaseOrderStatus.PartiallyReceived);

        // A second receipt that completes the order closes the loop.
        order.RecordReceipt(new Dictionary<PurchaseOrderLineId, decimal>
        {
            [order.Lines.Single().Id] = order.Lines.Single().OrderedQuantity,
        });
        order.Status.Should().Be(PurchaseOrderStatus.FullyReceived);
    }

    [Fact]
    public void RecordReceipt_FromADraft_IsRejected()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);

        Result outcome = order.RecordReceipt(new Dictionary<PurchaseOrderLineId, decimal>
        {
            [order.Lines.Single().Id] = 1m,
        });

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.invalid_state");
    }

    private static PurchaseOrder Draft(IReadOnlyList<PurchaseOrderLineSpec> lines)
        => PurchaseOrder.Create(Supplier, Store1, lines, Shelf, Now, "PHP", null).Value;

    private static PurchaseOrder PendingApproval()
    {
        PurchaseOrder order = Draft([Line(Coke, 10m, 100m)]);
        order.Submit(DocumentNumber.Create(DocumentType.PurchaseOrder, 2026, 1), Now);
        return order;
    }

    private static PurchaseOrder Ordered()
    {
        PurchaseOrder order = Approved();
        order.Send(Now.AddMinutes(5));
        return order;
    }

    private static PurchaseOrder Approved()
    {
        PurchaseOrder order = PendingApproval();
        order.Approve(Shelf, Now, order.GrandTotal);
        return order;
    }

    private static PurchaseOrderLineSpec Line(ProductId product, decimal quantity, decimal unitCost)
        => new(product, Case, quantity, unitCost);
}