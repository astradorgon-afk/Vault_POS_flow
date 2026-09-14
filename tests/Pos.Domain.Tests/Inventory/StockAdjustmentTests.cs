using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;

namespace Pos.Domain.Tests.Inventory;

/// <summary>
/// The stock adjustment's rules: the reason decides what an adjustment may do and
/// which ledger movement each line posts as, and the lifecycle only moves forward.
/// </summary>
public sealed class StockAdjustmentTests
{
    private static readonly LocationId Store = LocationId.New();
    private static readonly UserId Clerk = UserId.New();
    private static readonly UserId Manager = UserId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AdjustmentReasonCode.Damaged, InventoryState.Available, InventoryMovementType.Damage)]
    [InlineData(AdjustmentReasonCode.Broken, InventoryState.Damaged, InventoryMovementType.Damage)]
    [InlineData(AdjustmentReasonCode.Contaminated, InventoryState.Quarantine, InventoryMovementType.Damage)]
    [InlineData(AdjustmentReasonCode.Spoilage, InventoryState.Available, InventoryMovementType.Spoilage)]
    [InlineData(AdjustmentReasonCode.Loss, InventoryState.Available, InventoryMovementType.Loss)]
    [InlineData(AdjustmentReasonCode.Theft, InventoryState.Available, InventoryMovementType.Theft)]
    [InlineData(AdjustmentReasonCode.Expired, InventoryState.Available, InventoryMovementType.ExpiryQuarantine)]
    [InlineData(AdjustmentReasonCode.Expired, InventoryState.Expired, InventoryMovementType.ExpiryWriteOff)]
    public void WriteOffReasons_PostAsTheirOwnMovementType(
        AdjustmentReasonCode reason, InventoryState state, InventoryMovementType expected)
    {
        Result<StockAdjustment> created = Create(reason, null, Line(state, -3m));

        created.IsSuccess.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        created.Value.Lines.Should().ContainSingle().Which.MovementType.Should().Be(expected);
    }

    [Fact]
    public void Other_CanAddOrRemoveStock_AsAnApprovedCorrection()
    {
        Result<StockAdjustment> created = Create(
            AdjustmentReasonCode.Other, "Found a case behind the cold room door",
            Line(InventoryState.Available, 6m), Line(InventoryState.Damaged, -2m, ProductId.New()));

        created.IsSuccess.Should().BeTrue();
        created.Value.Lines.Should().OnlyContain(l => l.MovementType == InventoryMovementType.ApprovedStockAdjustment);
        created.Value.TotalAbsoluteValue.Should().Be((6m * 10m) + (2m * 10m));
    }

    [Fact]
    public void WriteOffs_CannotAddStock()
        => Create(AdjustmentReasonCode.Theft, null, Line(InventoryState.Available, 1m))
            .Errors.Should().ContainSingle(e => e.Code == "adjustment.must_remove_stock");

    [Fact]
    public void Other_NeedsMeaningfulNotes()
        => Create(AdjustmentReasonCode.Other, "fix", Line(InventoryState.Available, 1m))
            .Errors.Should().ContainSingle(e => e.Code == "adjustment.notes_required");

    [Theory]
    [InlineData(AdjustmentReasonCode.CountCorrection)]
    [InlineData(AdjustmentReasonCode.SupplierReturn)]
    [InlineData(AdjustmentReasonCode.TransitVarianceWriteOff)]
    public void ReasonsOwnedByOtherDocuments_AreRefused(AdjustmentReasonCode reason)
        => Create(reason, "Trying to bypass the proper document", Line(InventoryState.Available, -1m))
            .Errors.Should().ContainSingle(e => e.Code == "adjustment.reason_not_allowed");

    [Fact]
    public void StatesTheReasonCannotTouch_AreRefused()
    {
        Create(AdjustmentReasonCode.Expired, null, Line(InventoryState.Quarantine, -1m))
            .Errors.Should().ContainSingle(e => e.Code == "adjustment.state_not_allowed");

        Create(AdjustmentReasonCode.Loss, null, Line(InventoryState.InTransit, -1m))
            .Errors.Should().ContainSingle(e => e.Code == "adjustment.state_not_allowed");
    }

    [Fact]
    public void LineProblems_AreAllReported()
    {
        ProductId product = ProductId.New();

        Result<StockAdjustment> created = Create(
            AdjustmentReasonCode.Damaged,
            null,
            new StockAdjustmentLineSpec(product, null, InventoryState.Available, 0m, 10m),
            new StockAdjustmentLineSpec(ProductId.New(), null, InventoryState.Available, -1m, 10m, TracksBatches: true),
            new StockAdjustmentLineSpec(ProductId.New(), BatchId.New(), InventoryState.Available, -1m, -5m));

        created.Errors.Select(e => e.Code).Should().BeEquivalentTo(
            "adjustment.quantity_zero", "inventory_control.batch_required",
            "adjustment.unit_cost_negative", "inventory_control.batch_not_allowed");
    }

    [Fact]
    public void Lifecycle_SubmitThenPost_RecordsTheApproverAndNumber()
    {
        StockAdjustment adjustment = Create(AdjustmentReasonCode.Damaged, null, Line(InventoryState.Available, -2m)).Value;
        DocumentNumber number = DocumentNumber.Create(DocumentType.StockAdjustment, 2026, 7);

        adjustment.Post(number, Manager, Now)
            .Errors.Should().ContainSingle(e => e.Code == "adjustment.invalid_state");

        adjustment.Submit(Now).IsSuccess.Should().BeTrue();
        adjustment.Post(number, Manager, Now).IsSuccess.Should().BeTrue();

        adjustment.Status.Should().Be(StockAdjustmentStatus.Posted);
        adjustment.Number.Should().Be("ADJ-2026-000007");
        adjustment.DecidedByUserId.Should().Be(Manager);
        adjustment.Submit(Now).Errors.Should().ContainSingle(e => e.Code == "adjustment.invalid_state");
    }

    [Fact]
    public void Rejection_NeedsAReason_AndIsFinal()
    {
        StockAdjustment adjustment = Create(AdjustmentReasonCode.Loss, null, Line(InventoryState.Available, -1m)).Value;
        adjustment.Submit(Now);

        adjustment.Reject(Manager, " ", Now).Errors.Should().ContainSingle(e => e.Code == "inventory_control.reason_required");
        adjustment.Reject(Manager, "No evidence of loss", Now).IsSuccess.Should().BeTrue();

        adjustment.Status.Should().Be(StockAdjustmentStatus.Rejected);
        adjustment.Reverse(Manager, "Cannot reverse this", Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void OnlyAPostedAdjustment_CanBeReversed()
    {
        StockAdjustment adjustment = Create(AdjustmentReasonCode.Loss, null, Line(InventoryState.Available, -1m)).Value;
        adjustment.Reverse(Manager, "Too early to reverse", Now).IsFailure.Should().BeTrue();

        adjustment.Submit(Now);
        adjustment.Post(DocumentNumber.Create(DocumentType.StockAdjustment, 2026, 8), Manager, Now);

        adjustment.Reverse(Manager, "Stock was found the next morning", Now).IsSuccess.Should().BeTrue();
        adjustment.Status.Should().Be(StockAdjustmentStatus.Reversed);
        adjustment.ReversalReason.Should().Be("Stock was found the next morning");
    }

    private static StockAdjustmentLineSpec Line(InventoryState state, decimal delta, ProductId? product = null)
        => new(product ?? ProductId.New(), null, state, delta, 10m);

    private static Result<StockAdjustment> Create(
        AdjustmentReasonCode reason, string? notes, params StockAdjustmentLineSpec[] lines)
        => StockAdjustment.Create(Store, reason, notes, lines, Clerk, Now);
}
