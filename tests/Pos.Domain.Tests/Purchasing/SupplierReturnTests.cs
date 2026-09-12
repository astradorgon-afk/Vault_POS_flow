using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Purchasing;

namespace Pos.Domain.Tests.Purchasing;

/// <summary>
/// The supplier return lifecycle: Draft → PendingApproval → Approved →
/// Dispatched → Confirmed, the never-from-Available sourcing rule, and the
/// SRT numbering applied only at dispatch.
/// </summary>
public sealed class SupplierReturnTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
    private static readonly SupplierId Supplier = SupplierId.New();
    private static readonly LocationId Store1 = LocationId.New();
    private static readonly UserId Shelf = UserId.New();
    private static readonly UserId Manager = UserId.New();
    private static readonly ProductId Coke = ProductId.New();

    [Fact]
    public void Create_ValidDamagedLine_CreatesDraftReturn()
    {
        Result<SupplierReturn> result = Create(Line(10m, 100m));

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        SupplierReturn returnDocument = result.Value;

        returnDocument.Id.Should().NotBe(SupplierReturnId.Empty);
        returnDocument.Status.Should().Be(SupplierReturnStatus.Draft);
        returnDocument.SupplierId.Should().Be(Supplier);
        returnDocument.LocationId.Should().Be(Store1);
        returnDocument.CreatedByUserId.Should().Be(Shelf);
        returnDocument.CreatedAtUtc.Should().Be(Now);
        returnDocument.Number.Should().BeEmpty();
        returnDocument.TotalValue.Should().Be(1000m);

        SupplierReturnLine line = returnDocument.Lines.Single();
        line.LineNo.Should().Be(1);
        line.ProductId.Should().Be(Coke);
        line.SourceState.Should().Be(InventoryState.Damaged);
        line.Reason.Should().Be(SupplierReturnReason.Damaged);
        line.LineTotal.Should().Be(1000m);
    }

    [Fact]
    public void Create_EmptyReturn_IsRejected()
    {
        Result<SupplierReturn> result = SupplierReturn.Create(
            Supplier,
            Store1,
            [],
            Shelf,
            Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_empty");
    }

    [Fact]
    public void Create_AvailableSource_IsRejected()
    {
        SupplierReturnLineSpec available = new(Coke, null, InventoryState.Available, 10m, 100m, SupplierReturnReason.Damaged);

        Result<SupplierReturn> result = Create(available);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_source_state_invalid");
    }

    [Fact]
    public void Create_BatchTrackedLineWithoutBatch_IsRejected()
    {
        SupplierReturnLineSpec spec = new(Coke, null, InventoryState.Damaged, 10m, 100m, SupplierReturnReason.Damaged, TracksBatches: true);

        Result<SupplierReturn> result = Create(spec);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_batch_required");
    }

    [Fact]
    public void Create_NonBatchTrackedLineWithBatch_IsRejected()
    {
        SupplierReturnLineSpec spec = new(Coke, BatchId.New(), InventoryState.Damaged, 10m, 100m, SupplierReturnReason.Damaged, TracksBatches: false);

        Result<SupplierReturn> result = Create(spec);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_batch_not_allowed");
    }

    [Fact]
    public void Create_OtherReasonWithoutNotes_IsRejected()
    {
        SupplierReturnLineSpec spec = new(Coke, null, InventoryState.Damaged, 10m, 100m, SupplierReturnReason.Other);

        Result<SupplierReturn> result = Create(spec);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_other_reason_notes_required");
    }

    [Fact]
    public void Create_NonReturnableState_IsRejected()
    {
        SupplierReturnLineSpec spec = new(Coke, null, InventoryState.PendingInspection, 10m, 100m, SupplierReturnReason.WrongItem);

        Result<SupplierReturn> result = Create(spec);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_source_state_invalid");
    }

    [Fact]
    public void Create_NonPositiveQuantity_IsRejected()
    {
        Result<SupplierReturn> result = Create(Line(0m, 100m));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_quantity_invalid");
    }

    [Fact]
    public void Create_NegativeUnitCost_IsRejected()
    {
        Result<SupplierReturn> result = Create(Line(10m, -1m));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_unit_cost_invalid");
    }

    [Fact]
    public void Create_DuplicateLine_IsRejected()
    {
        SupplierReturnLineSpec first = Line(10m, 100m);
        SupplierReturnLineSpec duplicate = Line(2m, 100m);

        Result<SupplierReturn> result = Create(first, duplicate);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("purchasing.return_duplicate_line");
    }

    [Fact]
    public void Submit_Draft_TransitionsToPendingApproval()
    {
        SupplierReturn returnDocument = Create().Value;

        Result submitted = returnDocument.Submit(Now.AddMinutes(5));

        submitted.IsSuccess.Should().BeTrue();
        returnDocument.Status.Should().Be(SupplierReturnStatus.PendingApproval);
    }

    [Fact]
    public void Submit_Twice_Fails()
    {
        SupplierReturn returnDocument = Create().Value;
        returnDocument.Submit(Now);

        Result again = returnDocument.Submit(Now.AddMinutes(1));

        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("purchasing.return_invalid_state");
    }

    [Fact]
    public void Approve_PendingApproval_TransitionsToApproved()
    {
        SupplierReturn returnDocument = Create().Value;
        returnDocument.Submit(Now);

        Result approved = returnDocument.Approve(Manager, Now.AddMinutes(5));

        approved.IsSuccess.Should().BeTrue();
        returnDocument.Status.Should().Be(SupplierReturnStatus.Approved);
        returnDocument.ApprovedByUserId.Should().Be(Manager);
        returnDocument.ApprovedAtUtc.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public void Approve_BeforeSubmission_Fails()
    {
        SupplierReturn returnDocument = Create().Value;

        Result approved = returnDocument.Approve(Manager, Now);

        approved.IsFailure.Should().BeTrue();
        approved.Error.Code.Should().Be("purchasing.return_invalid_state");
    }

    [Fact]
    public void Reject_PendingApproval_ReturnsToDraft()
    {
        SupplierReturn returnDocument = Create().Value;
        returnDocument.Submit(Now);

        Result rejected = returnDocument.Reject(Now.AddMinutes(5));

        rejected.IsSuccess.Should().BeTrue();
        returnDocument.Status.Should().Be(SupplierReturnStatus.Draft);
        returnDocument.ApprovedByUserId.Should().BeNull();
    }

    [Fact]
    public void Dispatch_WithoutSupplierAuthorizationNumber_Fails()
    {
        SupplierReturn returnDocument = Approve(Create());
        returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 1));

        Result dispatched = returnDocument.Dispatch(Now.AddHours(1), " ");

        dispatched.IsFailure.Should().BeTrue();
        dispatched.Error.Code.Should().Be("purchasing.return_authorization_number_required");
    }

    [Fact]
    public void Dispatch_BeforeApproval_Fails()
    {
        SupplierReturn returnDocument = Create().Value;

        Result dispatched = returnDocument.Dispatch(Now, "AUTH-1");

        dispatched.IsFailure.Should().BeTrue();
        dispatched.Error.Code.Should().Be("purchasing.return_invalid_state");
    }

    [Fact]
    public void Dispatch_ApprovedWithAuthorization_TransitionsToDispatched()
    {
        SupplierReturn returnDocument = Approve(Create());
        returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 1));

        Result dispatched = returnDocument.Dispatch(Now.AddHours(1), "  RA-77  ");

        dispatched.IsSuccess.Should().BeTrue();
        returnDocument.Status.Should().Be(SupplierReturnStatus.Dispatched);
        returnDocument.SupplierAuthorizationNumber.Should().Be("RA-77");
        returnDocument.DispatchedAtUtc.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Confirm_Dispatched_TransitionsToConfirmed()
    {
        SupplierReturn returnDocument = Approve(Create());
        returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 1));
        returnDocument.Dispatch(Now.AddHours(1), "RA-77");

        Result confirmed = returnDocument.Confirm(Now.AddDays(2));

        confirmed.IsSuccess.Should().BeTrue();
        returnDocument.Status.Should().Be(SupplierReturnStatus.Confirmed);
        returnDocument.ConfirmedAtUtc.Should().Be(Now.AddDays(2));
    }

    [Fact]
    public void Confirm_BeforeDispatch_Fails()
    {
        SupplierReturn returnDocument = Approve(Create());
        returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 1));

        Result confirmed = returnDocument.Confirm(Now);

        confirmed.IsFailure.Should().BeTrue();
        confirmed.Error.Code.Should().Be("purchasing.return_invalid_state");
    }

    [Fact]
    public void AssignNumber_AfterDispatch_Fails()
    {
        SupplierReturn returnDocument = Approve(Create());
        returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 1));
        returnDocument.Dispatch(Now, "RA-77");

        Result numbered = returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 2));

        numbered.IsFailure.Should().BeTrue();
        numbered.Error.Code.Should().Be("purchasing.return_invalid_state");
    }

    [Fact]
    public void AssignNumber_Twice_Fails()
    {
        SupplierReturn returnDocument = Approve(Create());
        returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 1));

        Result again = returnDocument.AssignNumber(DocumentNumber.Create(DocumentType.SupplierReturn, 2026, 2));

        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("purchasing.return_already_numbered");
    }

    private static SupplierReturn Approve(Result<SupplierReturn> created)
    {
        SupplierReturn returnDocument = created.Value;
        returnDocument.Submit(Now);
        returnDocument.Approve(Manager, Now.AddMinutes(1));
        return returnDocument;
    }

    private static SupplierReturnLineSpec Line(decimal quantity, decimal unitCost)
        => new(Coke, null, InventoryState.Damaged, quantity, unitCost, SupplierReturnReason.Damaged);

    private static Result<SupplierReturn> Create(params SupplierReturnLineSpec[] lines)
        => SupplierReturn.Create(
            Supplier,
            Store1,
            lines.Length == 0 ? [Line(10m, 100m)] : lines,
            Shelf,
            Now);
}