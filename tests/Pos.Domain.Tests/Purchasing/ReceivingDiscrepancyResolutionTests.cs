using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Domain.Tests.Purchasing;

/// <summary>
/// Closing a receiving discrepancy: the outcome decision is recorded on the
/// discrepancy row exactly once, by an authorised user, and a resolved
/// discrepancy is immutable.
/// </summary>
public sealed class ReceivingDiscrepancyResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 9, 12);
    private static readonly PurchaseOrderId Order = PurchaseOrderId.New();
    private static readonly SupplierId Supplier = SupplierId.New();
    private static readonly LocationId Store1 = LocationId.New();
    private static readonly UserId Shelf = UserId.New();
    private static readonly UserId HQ = UserId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly PurchaseOrderLineId CokeLine = PurchaseOrderLineId.New();

    [Fact]
    public void Resolve_ShortageDiscrepancy_RecordsTheOutcomeIncludingAuthorisation()
    {
        GoodsReceipt receipt = ReceiptWithShortage();
        ReceivingDiscrepancy discrepancy = receipt.Discrepancies.Single();

        Result resolved = receipt.ResolveDiscrepancy(
            discrepancy.Id,
            ReceivingDiscrepancyResolutionOutcome.SupplierCredit,
            "Credit note CN-88 issued.",
            HQ,
            Now.AddDays(2));

        resolved.IsSuccess.Should().BeTrue(because: string.Join("; ", resolved.Errors.Select(e => e.Code)));
        discrepancy.ResolutionOutcome.Should().Be(ReceivingDiscrepancyResolutionOutcome.SupplierCredit);
        discrepancy.ResolutionNote.Should().Be("Credit note CN-88 issued.");
        discrepancy.ResolvedByUserId.Should().Be(HQ);
        discrepancy.ResolvedAtUtc.Should().Be(Now.AddDays(2));
    }

    [Fact]
    public void Resolve_Twice_FailsWithAlreadyResolved()
    {
        GoodsReceipt receipt = ReceiptWithShortage();
        ReceivingDiscrepancy discrepancy = receipt.Discrepancies.Single();
        receipt.ResolveDiscrepancy(discrepancy.Id, ReceivingDiscrepancyResolutionOutcome.Replacement, null, HQ, Now);

        Result again = receipt.ResolveDiscrepancy(
            discrepancy.Id,
            ReceivingDiscrepancyResolutionOutcome.WriteOff,
            "Correction after the fact.",
            HQ,
            Now.AddMinutes(1));

        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("purchasing.discrepancy_already_resolved");
        discrepancy.ResolutionOutcome.Should().Be(ReceivingDiscrepancyResolutionOutcome.Replacement);
    }

    [Fact]
    public void Resolve_UnknownDiscrepancy_Fails()
    {
        GoodsReceipt receipt = ReceiptWithShortage();

        Result resolved = receipt.ResolveDiscrepancy(
            ReceivingDiscrepancyId.New(),
            ReceivingDiscrepancyResolutionOutcome.NoAction,
            null,
            HQ,
            Now);

        resolved.IsFailure.Should().BeTrue();
        resolved.Error.Code.Should().Be("purchasing.discrepancy_unknown");
    }

    [Fact]
    public void Resolve_InvalidOutcome_Fails()
    {
        GoodsReceipt receipt = ReceiptWithShortage();
        ReceivingDiscrepancy discrepancy = receipt.Discrepancies.Single();

        Result resolved = receipt.ResolveDiscrepancy(
            discrepancy.Id,
            (ReceivingDiscrepancyResolutionOutcome)99,
            null,
            HQ,
            Now);

        resolved.IsFailure.Should().BeTrue();
        resolved.Error.Code.Should().Be("purchasing.discrepancy_resolution_outcome_invalid");
    }

    private static GoodsReceipt ReceiptWithShortage()
    {
        Result<GoodsReceipt> planned = Plan(received: 8m, ordered: 10m);
        planned.IsSuccess.Should().BeTrue(because: string.Join("; ", planned.Errors.Select(e => e.Code)));
        return planned.Value;
    }

    private static Result<GoodsReceipt> Plan(decimal received, decimal ordered)
    {
        GoodsReceiptLineSpec line = new(CokeLine, received, 0m, 0m, 0m, 100m);

        Dictionary<PurchaseOrderLineId, PurchaseOrderLineReceivingInfo> orderLines = new()
        {
            [CokeLine] = new PurchaseOrderLineReceivingInfo(1, Coke, ordered, 100m),
        };

        Dictionary<ProductId, ProductReceivingTrackingInfo> products = new()
        {
            [Coke] = new ProductReceivingTrackingInfo(false, false),
        };

        return GoodsReceipt.Create(
            Order,
            Supplier,
            Store1,
            [line],
            orderLines,
            new Dictionary<PurchaseOrderLineId, decimal>(),
            products,
            documentsMissing: false,
            BusinessDate,
            Shelf,
            Now);
    }
}