using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;

namespace Pos.Domain.Tests.Inventory;

/// <summary>
/// The ledger's structural invariants. If any of these regress, stock can be
/// created or destroyed without an explanation, which is the one thing this
/// system exists to prevent.
/// </summary>
public sealed class InventoryMovementGroupTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BusinessDate = new(2026, 3, 4);

    private static readonly LocationId Main = LocationId.New();
    private static readonly LocationId Store1 = LocationId.New();
    private static readonly LocationId ExtSupplier = LocationId.New();
    private static readonly LocationId ExtCustomer = LocationId.New();
    private static readonly LocationId ExtWriteOff = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly ProductId Rice = ProductId.New();

    [Fact]
    public void SupplierReceipt_IsBalanced_AndProducesTwoLegs()
    {
        MovementGroupSpec spec = Spec(
            InventoryMovementType.SupplierReceipt,
            ReferenceDocumentType.GoodsReceipt,
            "GRN-2026-000014",
            [
                Leg(ExtSupplier, LocationKind.External, InventoryState.External, -98m, 1200m),
                Leg(Main, LocationKind.MainWarehouse, InventoryState.PendingInspection, +98m, 1200m),
            ]);

        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(spec, RecordedAt);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        result.Value.Movements.Should().HaveCount(2);
        result.Value.Movements.Sum(m => m.QuantityDelta).Should().Be(0m);
        result.Value.Movements.Select(m => m.LegNumber).Should().Equal((short)1, (short)2);
        result.Value.Movements.Should().OnlyContain(m => m.MovementGroupId == result.Value.Id);
    }

    [Fact]
    public void TransferDispatch_MovesAvailableToInTransit_AtTheSourceLocation()
    {
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.TransferDispatch,
                ReferenceDocumentType.TransferShipment,
                "TRF-2026-000001",
                [
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, -100m, 45m),
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, +100m, 45m),
                ]),
            RecordedAt);

        result.IsSuccess.Should().BeTrue();

        // Custody stays with the source: in-transit is held at Main, not Store 1.
        result.Value.Movements.Should().OnlyContain(m => m.LocationId == Main);
        result.Value.Movements.Sum(m => m.QuantityDelta).Should().Be(0m);
    }

    [Fact]
    public void TransferReceipt_WithShortage_ParksTheDifferenceInTransitVariance()
    {
        // 100 dispatched, 98 counted at the destination. The missing 2 must stay
        // on the books at the source until someone explains them.
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.TransferReceipt,
                ReferenceDocumentType.TransferReceipt,
                "TRC-2026-000001",
                [
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, -100m, 45m),
                    Leg(Store1, LocationKind.Store, InventoryState.Available, +98m, 45m),
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.TransitVariance, +2m, 45m),
                ]),
            RecordedAt);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        result.Value.Movements.Sum(m => m.QuantityDelta).Should().Be(0m);

        result.Value.Movements
            .Single(m => m.State == InventoryState.TransitVariance)
            .Should().Match<InventoryMovement>(m => m.LocationId == Main && m.QuantityDelta == 2m);
    }

    [Fact]
    public void UnbalancedGroup_IsRejected()
    {
        // The classic bug this design exists to make impossible: 100 leaves,
        // 98 arrives, and the difference quietly evaporates.
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.TransferReceipt,
                ReferenceDocumentType.TransferReceipt,
                "TRC-2026-000002",
                [
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, -100m, 45m),
                    Leg(Store1, LocationKind.Store, InventoryState.Available, +98m, 45m),
                ]),
            RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.movement_group_not_balanced");
    }

    [Fact]
    public void ShortageOfOneProduct_CannotBeNettedAgainstSurplusOfAnother()
    {
        // Totals sum to zero, but per product they do not. Rice is not Coke.
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.TransferReceipt,
                ReferenceDocumentType.TransferReceipt,
                "TRC-2026-000003",
                [
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, -10m, 45m, Coke),
                    Leg(Store1, LocationKind.Store, InventoryState.Available, +8m, 45m, Coke),
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, -5m, 1200m, Rice),
                    Leg(Store1, LocationKind.Store, InventoryState.Available, +7m, 1200m, Rice),
                ]),
            RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().OnlyContain(e => e.Code == "inventory.movement_group_not_balanced");
    }

    [Fact]
    public void SingleLegGroup_IsRejected()
    {
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.SupplierReceipt,
                ReferenceDocumentType.GoodsReceipt,
                "GRN-2026-000015",
                [Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, +50m, 10m)]),
            RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("inventory.movement_group_too_few_legs");
    }

    [Fact]
    public void ZeroQuantityLeg_IsRejected()
    {
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.SupplierReceipt,
                ReferenceDocumentType.GoodsReceipt,
                "GRN-2026-000016",
                [
                    Leg(ExtSupplier, LocationKind.External, InventoryState.External, 0m, 10m),
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, 0m, 10m),
                ]),
            RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.movement_zero_quantity");
    }

    [Fact]
    public void PosSale_CannotTakeStockOutOfQuarantine()
    {
        // Quarantined goods are never sellable. This is the rule that stops
        // unauthorized inventory from reaching a customer.
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.PosSale,
                ReferenceDocumentType.Sale,
                "SAL-2026-D03-000812",
                [
                    Leg(Store1, LocationKind.Store, InventoryState.Quarantine, -2m, 45m),
                    Leg(ExtCustomer, LocationKind.External, InventoryState.External, +2m, 45m),
                ]),
            RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.source_state_not_allowed");
    }

    [Fact]
    public void QuarantineRelease_RequiresAnApprover()
    {
        MovementGroupSpec spec = Spec(
            InventoryMovementType.QuarantineRelease,
            ReferenceDocumentType.QuarantineIncident,
            "QRT-2026-000014",
            [
                Leg(Store1, LocationKind.Store, InventoryState.Quarantine, -12m, 30m),
                Leg(Store1, LocationKind.Store, InventoryState.Available, +12m, 30m),
            ]) with
        {
            Actor = new LedgerActor(UserId.New(), ApprovedBy: null, Device: null, CorrelationId.New()),
        };

        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(spec, RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.approver_required");
    }

    [Fact]
    public void WriteOff_RequiresAReasonCode()
    {
        MovementGroupSpec spec = Spec(
            InventoryMovementType.Theft,
            ReferenceDocumentType.StockAdjustment,
            "ADJ-2026-000003",
            [
                Leg(Store1, LocationKind.Store, InventoryState.Available, -3m, 45m),
                Leg(ExtWriteOff, LocationKind.External, InventoryState.External, +3m, 45m),
            ]) with
        { ReasonCode = null };

        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(spec, RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.reason_code_required");
    }

    [Fact]
    public void ReasonOfOther_RequiresExplanatoryNotes()
    {
        MovementGroupSpec spec = Spec(
            InventoryMovementType.Loss,
            ReferenceDocumentType.StockAdjustment,
            "ADJ-2026-000004",
            [
                Leg(Store1, LocationKind.Store, InventoryState.Available, -1m, 45m),
                Leg(ExtWriteOff, LocationKind.External, InventoryState.External, +1m, 45m),
            ]) with
        { ReasonCode = AdjustmentReasonCode.Other, Notes = "oops" };

        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(spec, RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.reason_notes_required");
    }

    [Fact]
    public void ExternalState_IsRejectedOnAnInternalLocation()
    {
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.PosSale,
                ReferenceDocumentType.Sale,
                "SAL-2026-D03-000813",
                [
                    Leg(Store1, LocationKind.Store, InventoryState.Available, -1m, 45m),
                    Leg(Store1, LocationKind.Store, InventoryState.External, +1m, 45m),
                ]),
            RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.state_invalid_for_location");
    }

    [Fact]
    public void BatchTrackedProduct_MustCarryABatch()
    {
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.SupplierReceipt,
                ReferenceDocumentType.GoodsReceipt,
                "GRN-2026-000017",
                [
                    Leg(ExtSupplier, LocationKind.External, InventoryState.External, -10m, 5m, Rice, tracksBatches: true),
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, +10m, 5m, Rice, tracksBatches: true),
                ]),
            RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.batch_required");
    }

    [Fact]
    public void Reversal_MustNameTheGroupItReverses()
    {
        MovementGroupSpec spec = Spec(
            InventoryMovementType.Reversal,
            ReferenceDocumentType.Sale,
            "SAL-2026-D03-000812",
            [
                Leg(ExtCustomer, LocationKind.External, InventoryState.External, -2m, 45m),
                Leg(Store1, LocationKind.Store, InventoryState.Available, +2m, 45m),
            ]) with
        { ReasonCode = AdjustmentReasonCode.CountCorrection, ReversesMovementGroupId = null };

        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(spec, RecordedAt);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "inventory.reversal_missing_original");
    }

    [Fact]
    public void TotalValueDelta_IsCostTimesQuantity_AndNetsToZeroForATransfer()
    {
        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(
            Spec(
                InventoryMovementType.TransferDispatch,
                ReferenceDocumentType.TransferShipment,
                "TRF-2026-000002",
                [
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.Available, -12.5m, 3.3333m),
                    Leg(Main, LocationKind.MainWarehouse, InventoryState.InTransit, +12.5m, 3.3333m),
                ]),
            RecordedAt);

        result.IsSuccess.Should().BeTrue();
        // 12.5 x 3.3333 = 41.66625; intermediate values round to 4 dp
        // half-to-even, and symmetrically, so the group still nets to zero.
        result.Value.Movements[0].TotalValueDelta.Should().Be(-41.6662m);
        result.Value.Movements[1].TotalValueDelta.Should().Be(41.6662m);
        result.Value.TotalValueDelta.Should().Be(0m);
    }

    [Fact]
    public void RecordedAtUtc_ComesFromTheServer_NotTheReportedOccurrenceTime()
    {
        DateTimeOffset deviceClaimed = RecordedAt.AddHours(-9);

        MovementGroupSpec spec = Spec(
            InventoryMovementType.PosSale,
            ReferenceDocumentType.Sale,
            "SAL-2026-D03-000814",
            [
                Leg(Store1, LocationKind.Store, InventoryState.Available, -1m, 45m),
                Leg(ExtCustomer, LocationKind.External, InventoryState.External, +1m, 45m),
            ]) with
        { OccurredAtUtc = deviceClaimed };

        Result<InventoryMovementGroup> result = InventoryMovementGroup.Create(spec, RecordedAt);

        result.IsSuccess.Should().BeTrue();
        result.Value.Movements.Should().OnlyContain(m => m.RecordedAtUtc == RecordedAt);
        result.Value.Movements.Should().OnlyContain(m => m.OccurredAtUtc == deviceClaimed);
    }

    [Fact]
    public void EveryDefinedMovementType_HasAUsableRule()
    {
        foreach (InventoryMovementType type in Enum.GetValues<InventoryMovementType>())
        {
            MovementTypeRules.IsDefined(type).Should().BeTrue($"movement type {type} must have a ledger rule");

            MovementTypeRule rule = MovementTypeRules.For(type);
            rule.AllowedSourceStates.Should().NotBeEmpty();
            rule.AllowedDestinationStates.Should().NotBeEmpty();
            rule.PermissionCode.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void ExternalState_IsOnlyValidOnExternalLocations()
    {
        MovementTypeRules.IsStateValidForLocation(LocationKind.External, InventoryState.External).Should().BeTrue();
        MovementTypeRules.IsStateValidForLocation(LocationKind.External, InventoryState.Available).Should().BeFalse();
        MovementTypeRules.IsStateValidForLocation(LocationKind.Store, InventoryState.External).Should().BeFalse();
        MovementTypeRules.IsStateValidForLocation(LocationKind.MainWarehouse, InventoryState.Available).Should().BeTrue();
    }

    [Fact]
    public void OnlyAvailableStock_IsSellableOrTransferable()
    {
        foreach (InventoryState state in Enum.GetValues<InventoryState>())
        {
            bool expected = state == InventoryState.Available;
            state.IsSellable().Should().Be(expected, $"{state} sellability");
            state.IsTransferable().Should().Be(expected, $"{state} transferability");
        }
    }

    private static MovementLegSpec Leg(
        LocationId location,
        LocationKind kind,
        InventoryState state,
        decimal quantity,
        decimal unitCost,
        ProductId? product = null,
        bool tracksBatches = false)
        => new(product ?? Coke, null, location, kind, state, quantity, unitCost, tracksBatches);

    private static MovementGroupSpec Spec(
        InventoryMovementType type,
        ReferenceDocumentType documentType,
        string documentNumber,
        IReadOnlyList<MovementLegSpec> legs)
        => new(
            EventId.New(),
            type,
            documentType,
            Guid.CreateVersion7(),
            documentNumber,
            legs,
            new LedgerActor(UserId.New(), UserId.New(), DeviceId.New(), CorrelationId.New()),
            RecordedAt,
            BusinessDate,
            ReasonCode: AdjustmentReasonCode.CountCorrection,
            Notes: "Recorded by an automated test.");
}
