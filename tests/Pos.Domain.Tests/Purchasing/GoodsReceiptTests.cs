using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Purchasing;

namespace Pos.Domain.Tests.Purchasing;

/// <summary>
/// The goods receipt's disposition planning: expected quantities are computed
/// from the order and the cumulative received total, the accepted / refused /
/// quarantined split follows the receiving policy, and every discrepancy is
/// preserved as a row that drives the follow-up work.
/// </summary>
public sealed class GoodsReceiptTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 12);

    private static readonly PurchaseOrderId Order = PurchaseOrderId.New();
    private static readonly SupplierId Supplier = SupplierId.New();
    private static readonly LocationId Store1 = LocationId.New();
    private static readonly UserId Shelf = UserId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly PurchaseOrderLineId CokeLine = PurchaseOrderLineId.New();

    [Fact]
    public void Create_CleanDelivery_PostsWithNoDiscrepancies()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10m), ordered: 10m, poUnitCost: 100m);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        GoodsReceipt receipt = result.Value;

        receipt.Id.Should().NotBe(GoodsReceiptId.Empty);
        receipt.PurchaseOrderId.Should().Be(Order);
        receipt.SupplierId.Should().Be(Supplier);
        receipt.DestinationLocationId.Should().Be(Store1);
        receipt.ReceivedByUserId.Should().Be(Shelf);
        receipt.ReceivedAtUtc.Should().Be(Now);
        receipt.BusinessDate.Should().Be(BusinessDate);
        receipt.Status.Should().Be(GoodsReceiptStatus.Posted);
        receipt.DocumentsMissing.Should().BeFalse();
        receipt.CostVariancePendingApproval.Should().BeFalse();
        receipt.CostVarianceValueAtStake.Should().Be(0m);
        receipt.Lines.Should().ContainSingle();
        receipt.Discrepancies.Should().BeEmpty();
        receipt.Number.Should().BeEmpty();

        GoodsReceiptLine line = receipt.Lines.Single();
        line.LineNo.Should().Be(1);
        line.QuantityExpected.Should().Be(10m);
        line.QuantityReceived.Should().Be(10m);
        line.QuantityDamaged.Should().Be(0m);
        line.QuantityWrongItem.Should().Be(0m);
        line.QuantityExpired.Should().Be(0m);
        line.OverageBeyondTolerance.Should().Be(0m);
        line.QuantityAccepted.Should().Be(10m);
        line.AcceptedState.Should().Be(InventoryState.PendingInspection);
        line.UnitCost.Should().Be(100m);
        line.CostVariancePercent.Should().Be(0m);
    }

    [Fact]
    public void Create_PartialDelivery_RecordsTheShortageInsteadOfAnOverage()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 8m), ordered: 10m, poUnitCost: 100m);

        result.IsSuccess.Should().BeTrue();
        GoodsReceipt receipt = result.Value;
        GoodsReceiptLine line = receipt.Lines.Single();

        line.QuantityExpected.Should().Be(10m);
        line.QuantityAccepted.Should().Be(8m);

        ReceivingDiscrepancy discrepancy = receipt.Discrepancies.Single();
        discrepancy.Kind.Should().Be(ReceivingDiscrepancyKind.Shortage);
        discrepancy.LineNo.Should().Be(1);
        discrepancy.Quantity.Should().Be(2m);
        discrepancy.ValueImpact.Should().Be(200m);
    }

    [Fact]
    public void Create_OverageWithinTolerance_IsAcceptedWithoutError()
    {
        // 10.49 against an expected 10.00 is a 4.9% overage, under the 5%
        // tolerance (0.50).
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10.49m), ordered: 10m, poUnitCost: 100m);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        GoodsReceiptLine line = result.Value.Lines.Single();

        line.QuantityAccepted.Should().Be(10.49m);
        line.OverageBeyondTolerance.Should().Be(0m);
        result.Value.Discrepancies.Should().BeEmpty();
    }

    [Fact]
    public void Create_OverageBeyondTolerance_IsAccepted_AndTheExcessQuarantined()
    {
        // 12.00 against 10.00 expected: overage 2.00, tolerance 0.50, excess 1.50.
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 12m), ordered: 10m, poUnitCost: 100m);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        GoodsReceiptLine line = result.Value.Lines.Single();

        line.QuantityExpected.Should().Be(10m);
        line.OverageBeyondTolerance.Should().Be(1.5m);
        line.QuantityAccepted.Should().Be(10.5m);

        ReceivingDiscrepancy discrepancy = result.Value.Discrepancies.Single();
        discrepancy.Kind.Should().Be(ReceivingDiscrepancyKind.Overage);
        discrepancy.Quantity.Should().Be(1.5m);
        discrepancy.ValueImpact.Should().Be(150m);
    }

    [Fact]
    public void Create_RefusedGoods_ProduceDamageWrongAndExpiredDiscrepancies()
    {
        Result<GoodsReceipt> result = Plan(
            Coke,
            Line(received: 10m, damaged: 2m, wrongItem: 1m, expired: 1m),
            ordered: 10m,
            poUnitCost: 100m);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        GoodsReceiptLine line = result.Value.Lines.Single();

        line.QuantityAccepted.Should().Be(6m);
        result.Value.Discrepancies.Should().HaveCount(3);
        result.Value.Discrepancies.Select(d => d.Kind)
            .Should().Equal(ReceivingDiscrepancyKind.Damaged, ReceivingDiscrepancyKind.WrongItem, ReceivingDiscrepancyKind.Expired);
        result.Value.Discrepancies.Single(d => d.Kind == ReceivingDiscrepancyKind.Damaged).Quantity.Should().Be(2m);
        result.Value.Discrepancies.Single(d => d.Kind == ReceivingDiscrepancyKind.WrongItem).Quantity.Should().Be(1m);
        result.Value.Discrepancies.Single(d => d.Kind == ReceivingDiscrepancyKind.Expired).Quantity.Should().Be(1m);
    }

    [Fact]
    public void Create_MissingDocuments_RecordsTheDiscrepancy_ButNoLedgerSplits()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10m), ordered: 10m, poUnitCost: 100m, documentsMissing: true);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        GoodsReceipt receipt = result.Value;

        receipt.DocumentsMissing.Should().BeTrue();

        ReceivingDiscrepancy discrepancy = receipt.Discrepancies.Single();
        discrepancy.Kind.Should().Be(ReceivingDiscrepancyKind.MissingDocuments);
        discrepancy.Quantity.Should().Be(0m);
        discrepancy.ValueImpact.Should().Be(0m);
        discrepancy.LineNo.Should().Be(1);
    }

    [Fact]
    public void Create_SecondReceipt_ComputesExpectedFromTheCumulativeTotal()
    {
        // Ten ordered; the first receipt took five, so this line expects five.
        Result<GoodsReceipt> result = Plan(
            Coke,
            Line(received: 3m),
            ordered: 10m,
            poUnitCost: 100m,
            receivedByLine: new Dictionary<PurchaseOrderLineId, decimal> { [CokeLine] = 5m });

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        GoodsReceiptLine line = result.Value.Lines.Single();

        line.QuantityExpected.Should().Be(5m);
        line.QuantityAccepted.Should().Be(3m);

        result.Value.Discrepancies.Single().Kind.Should().Be(ReceivingDiscrepancyKind.Shortage);
        result.Value.Discrepancies.Single().Quantity.Should().Be(2m);
    }

    [Fact]
    public void Create_DuplicateOrderLineWithinReceipt_IsRejected()
    {
        GoodsReceiptLineSpec first = Line(received: 4m);
        GoodsReceiptLineSpec second = new(CokeLine, 3m, 0m, 0m, 0m, 100m);

        Result<GoodsReceipt> result = Plan(Coke, [first, second], ordered: 10m, poUnitCost: 100m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_duplicate_line");
    }

    [Fact]
    public void Create_UnknownOrderLine_IsRejected()
    {
        Result<GoodsReceipt> result = Plan(
            Coke,
            [new GoodsReceiptLineSpec(PurchaseOrderLineId.New(), 4m, 0m, 0m, 0m, 100m)],
            ordered: 10m,
            poUnitCost: 100m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_unknown_line");
    }

    [Fact]
    public void Create_NothingReceived_IsRejected()
    {
        Result<GoodsReceipt> noSpecs = Plan(Coke, [], ordered: 10m, poUnitCost: 100m);

        noSpecs.IsFailure.Should().BeTrue();
        noSpecs.Error.Code.Should().Be("purchasing.receipt_nothing_received");

        // A delivery of zero counted units is equally not a delivery.
        Result<GoodsReceipt> zeroQuantity = Plan(Coke, Line(received: 0m), ordered: 10m, poUnitCost: 100m);

        zeroQuantity.IsFailure.Should().BeTrue();
        zeroQuantity.Error.Code.Should().Be("purchasing.receipt_nothing_received");
    }

    [Fact]
    public void Create_RejectedMoreThanReceived_IsRejected()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 4m, damaged: 5m), ordered: 10m, poUnitCost: 100m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_rejected_exceeds_received");
    }

    [Fact]
    public void Create_NegativeQuantity_IsRejected()
    {
        GoodsReceiptLineSpec line = new(CokeLine, -1m, 0m, 0m, 0m, 100m);

        Result<GoodsReceipt> result = Plan(Coke, [line], ordered: 10m, poUnitCost: 100m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_negative_quantity");
    }

    [Fact]
    public void Create_BatchTrackedProduct_RequiresALotNumber()
    {
        Result<GoodsReceipt> result = Plan(
            Coke,
            Line(received: 10m),
            ordered: 10m,
            poUnitCost: 100m,
            tracksBatches: true);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_batch_required");
    }

    [Fact]
    public void Create_LotProvidedForNonBatchProduct_IsRejected()
    {
        GoodsReceiptLineSpec line = new(CokeLine, 10m, 0m, 0m, 0m, 100m, LotNumber: "L-1");

        Result<GoodsReceipt> result = Plan(Coke, [line], ordered: 10m, poUnitCost: 100m);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_lot_not_allowed");
    }

    [Fact]
    public void Create_ExpiryTrackedProduct_RequiresAnExpiryDate()
    {
        GoodsReceiptLineSpec line = new(CokeLine, 10m, 0m, 0m, 0m, 100m, LotNumber: "L-1");

        Result<GoodsReceipt> result = Plan(
            Coke,
            [line],
            ordered: 10m,
            poUnitCost: 100m,
            tracksBatches: true,
            tracksExpiry: true);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_expires_on_required");
    }

    [Fact]
    public void Create_ExpiredOnArrival_IsRejected_UnlessEveryUnitIsRefused()
    {
        // Only part of the lot is marked refused: the past-dated goods would
        // leak into stock, which is exactly what the rule forbids.
        GoodsReceiptLineSpec partiallyExpired = new(
            CokeLine, 10m, 0m, 0m, 1m, 100m, LotNumber: "L-1", ExpiresOn: BusinessDate.AddDays(-1));

        Result<GoodsReceipt> refused = Plan(
            Coke,
            [partiallyExpired],
            ordered: 10m,
            poUnitCost: 100m,
            tracksBatches: true,
            tracksExpiry: true);

        refused.IsFailure.Should().BeTrue();
        refused.Errors.Should().Contain(e => e.Code == "purchasing.receipt_expired_on_arrival");

        // Every counted unit refused as expired: the lot posts to quarantine.
        GoodsReceiptLineSpec allExpired = new(
            CokeLine, 10m, 0m, 0m, 10m, 100m, LotNumber: "L-1", ExpiresOn: BusinessDate.AddDays(-1));

        Result<GoodsReceipt> accepted = Plan(
            Coke,
            [allExpired],
            ordered: 10m,
            poUnitCost: 100m,
            tracksBatches: true,
            tracksExpiry: true);

        accepted.IsSuccess.Should().BeTrue(because: string.Join("; ", accepted.Errors.Select(e => e.Code)));
        accepted.Value.Lines.Single().QuantityAccepted.Should().Be(0m);
        accepted.Value.Discrepancies.Single().Kind.Should().Be(ReceivingDiscrepancyKind.Expired);
    }

    [Fact]
    public void Create_ExpiryBeforeManufacture_IsRejected()
    {
        GoodsReceiptLineSpec line = new(
            CokeLine, 10m, 0m, 0m, 0m, 100m,
            LotNumber: "L-1",
            ManufacturedOn: BusinessDate,
            ExpiresOn: BusinessDate.AddDays(-1));

        Result<GoodsReceipt> result = Plan(
            Coke,
            [line],
            ordered: 10m,
            poUnitCost: 100m,
            tracksBatches: true,
            tracksExpiry: true);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "purchasing.receipt_expiry_before_manufacture");
    }

    [Fact]
    public void Create_CostVarianceWithinTolerance_NeedsNoApproval()
    {
        // 4% deviation against a 5% tolerance.
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10m, unitCost: 104m), ordered: 10m, poUnitCost: 100m);

        result.IsSuccess.Should().BeTrue();
        result.Value.CostVariancePendingApproval.Should().BeFalse();
        result.Value.CostVarianceValueAtStake.Should().Be(0m);
    }

    [Fact]
    public void Create_CostVarianceBeyondTolerance_FlagsTheStake()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10m, unitCost: 110m), ordered: 10m, poUnitCost: 100m);

        result.IsSuccess.Should().BeTrue();
        GoodsReceipt receipt = result.Value;

        receipt.CostVariancePendingApproval.Should().BeTrue();
        receipt.CostVarianceValueAtStake.Should().Be(1100m);
        receipt.Lines.Single().CostVariancePercent.Should().Be(10m);
        receipt.Lines.Single().CostVarianceBeyondTolerance.Should().BeTrue();
    }

    [Fact]
    public void GrantCostVarianceApproval_RecordsTheApprover_OnTheDeviatingLines()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10m, unitCost: 110m), ordered: 10m, poUnitCost: 100m);
        GoodsReceipt receipt = result.Value;

        Result outcome = receipt.GrantCostVarianceApproval(Shelf, Now);

        outcome.IsSuccess.Should().BeTrue();
        GoodsReceiptLine line = receipt.Lines.Single();
        line.CostVarianceApprovedByUserId.Should().Be(Shelf);
        line.CostVarianceApprovedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void GrantCostVarianceApproval_WhenThereIsNoVariance_IsRejected()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10m), ordered: 10m, poUnitCost: 100m);

        Result outcome = result.Value.GrantCostVarianceApproval(Shelf, Now);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("purchasing.receipt_no_variance_to_approve");
    }

    [Fact]
    public void AssignNumber_StampsTheGrn_AndRefusesASecond()
    {
        Result<GoodsReceipt> result = Plan(Coke, Line(received: 10m), ordered: 10m, poUnitCost: 100m);
        GoodsReceipt receipt = result.Value;

        Result assigned = receipt.AssignNumber(DocumentNumber.Create(DocumentType.GoodsReceipt, 2026, 42));

        assigned.IsSuccess.Should().BeTrue();
        receipt.Number.Should().Be("GRN-2026-000042");

        Result again = receipt.AssignNumber(DocumentNumber.Create(DocumentType.GoodsReceipt, 2026, 43));

        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("purchasing.receipt_already_numbered");
    }

    [Fact]
    public void Create_EmptyOrderId_IsRejected()
    {
        Result<GoodsReceipt> result = Plan(
            Coke,
            Line(received: 10m),
            ordered: 10m,
            poUnitCost: 100m,
            orderId: PurchaseOrderId.Empty);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.order_required");
    }

    [Fact]
    public void Create_BatchTrackedDelivery_KeepsTheLotForTheLedger()
    {
        GoodsReceiptLineSpec line = new(CokeLine, 10m, 0m, 0m, 0m, 100m, LotNumber: "LOT-9", ManufacturedOn: BusinessDate.AddDays(-30), ExpiresOn: BusinessDate.AddDays(270));

        Result<GoodsReceipt> result = Plan(
            Coke,
            [line],
            ordered: 10m,
            poUnitCost: 100m,
            tracksBatches: true,
            tracksExpiry: true);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        GoodsReceiptLine planned = result.Value.Lines.Single();

        planned.LotNumber.Should().Be("LOT-9");
        planned.ManufacturedOn.Should().Be(BusinessDate.AddDays(-30));
        planned.ExpiresOn.Should().Be(BusinessDate.AddDays(270));
    }

    private static GoodsReceiptLineSpec Line(decimal received, decimal damaged = 0m, decimal wrongItem = 0m, decimal expired = 0m, decimal unitCost = 100m)
        => new(CokeLine, received, damaged, wrongItem, expired, unitCost);

    private static Result<GoodsReceipt> Plan(
        ProductId product,
        GoodsReceiptLineSpec line,
        decimal ordered,
        decimal poUnitCost,
        IReadOnlyDictionary<PurchaseOrderLineId, decimal>? receivedByLine = null,
        bool tracksBatches = false,
        bool tracksExpiry = false,
        bool documentsMissing = false,
        PurchaseOrderId? orderId = null)
        => Plan(product, [line], ordered, poUnitCost, receivedByLine, tracksBatches, tracksExpiry, documentsMissing, orderId);

    private static Result<GoodsReceipt> Plan(
        ProductId product,
        IReadOnlyList<GoodsReceiptLineSpec> lines,
        decimal ordered,
        decimal poUnitCost,
        IReadOnlyDictionary<PurchaseOrderLineId, decimal>? receivedByLine = null,
        bool tracksBatches = false,
        bool tracksExpiry = false,
        bool documentsMissing = false,
        PurchaseOrderId? orderId = null)
    {
        Dictionary<PurchaseOrderLineId, PurchaseOrderLineReceivingInfo> orderLines = new()
        {
            [CokeLine] = new PurchaseOrderLineReceivingInfo(1, product, ordered, poUnitCost),
        };

        Dictionary<ProductId, ProductReceivingTrackingInfo> products = new()
        {
            [product] = new ProductReceivingTrackingInfo(tracksBatches, tracksExpiry),
        };

        return GoodsReceipt.Create(
            orderId ?? Order,
            Supplier,
            Store1,
            lines,
            orderLines,
            receivedByLine ?? new Dictionary<PurchaseOrderLineId, decimal>(),
            products,
            documentsMissing,
            BusinessDate,
            Shelf,
            Now);
    }
}