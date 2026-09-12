using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Transfers;

namespace Pos.Domain.Tests.Transfers;

/// <summary>
/// The transfer order's lifecycle invariants: Picking needs prior approval, the
/// TRF number is allocated only at dispatch, a dispatch cannot be cancelled
/// after anything arrived, shortfalls become discrepancies, and nothing can be
/// verified while a discrepancy is open.
/// </summary>
public sealed class TransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 30, 0, TimeSpan.Zero);

    private static readonly LocationId Warehouse = LocationId.New();
    private static readonly LocationId Store = LocationId.New();
    private static readonly UserId Requester = UserId.New();
    private static readonly UserId Approver = UserId.New();
    private static readonly UserId Picker = UserId.New();
    private static readonly UserId Receiver = UserId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly ProductId Rice = ProductId.New();
    private static readonly BatchId BatchA = BatchId.New();
    private static readonly BatchId BatchB = BatchId.New();

    private static readonly DocumentNumber TrfNumber = DocumentNumber.Create(DocumentType.TransferOrder, 2026, 7);
    private static readonly DocumentNumber ShpNumber = DocumentNumber.Create(DocumentType.TransferShipment, 2026, 3);
    private static readonly DocumentNumber TrcNumber = DocumentNumber.Create(DocumentType.TransferReceipt, 2026, 11);

    [Fact]
    public void Create_ProducesADraftWithLinesAndACreatedEvent()
    {
        Result<Transfer> result = Transfer.Create(
            Warehouse,
            Store,
            [Spec(Coke, 10m), Spec(Rice, 5m, "picking note")],
            Requester,
            Now);

        result.IsSuccess.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Code)));
        Transfer transfer = result.Value;

        transfer.Id.Should().NotBe(TransferOrderId.Empty);
        transfer.Status.Should().Be(TransferStatus.Draft);
        transfer.Number.Should().BeEmpty();
        transfer.SourceLocationId.Should().Be(Warehouse);
        transfer.DestinationLocationId.Should().Be(Store);
        transfer.CreatedByUserId.Should().Be(Requester);
        transfer.CreatedAtUtc.Should().Be(Now);
        transfer.Lines.Should().HaveCount(2);
        transfer.Lines.Select(l => l.LineNo).Should().Equal(1, 2);
        transfer.Lines[0].ProductId.Should().Be(Coke);
        transfer.Lines[1].Note.Should().Be("picking note");
        transfer.CustodyEvents.Select(e => e.Kind).Should().Equal(TransferCustodyEventKind.Created);
    }

    [Fact]
    public void Create_MissingLocations_AreRejected()
    {
        Result<Transfer> noSource = Transfer.Create(
            LocationId.Empty,
            Store,
            [Spec(Coke, 1m)],
            Requester,
            Now);

        noSource.IsFailure.Should().BeTrue();
        noSource.Error.Code.Should().Be("transfer.source_required");

        Result<Transfer> noDestination = Transfer.Create(
            Warehouse,
            LocationId.Empty,
            [Spec(Coke, 1m)],
            Requester,
            Now);

        noDestination.IsFailure.Should().BeTrue();
        noDestination.Error.Code.Should().Be("transfer.destination_required");
    }

    [Fact]
    public void Create_SameSourceAndDestination_IsRejected()
    {
        Result<Transfer> result = Transfer.Create(
            Warehouse,
            Warehouse,
            [Spec(Coke, 1m)],
            Requester,
            Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("transfer.same_location");
    }

    [Fact]
    public void Create_WithoutLinesOrWithZeroQuantities_IsRejected()
    {
        Result<Transfer> empty = Transfer.Create(Warehouse, Store, [], Requester, Now);
        empty.IsFailure.Should().BeTrue();
        empty.Error.Code.Should().Be("transfer.empty");

        Result<Transfer> zero = Transfer.Create(Warehouse, Store, [Spec(Coke, 0m)], Requester, Now);
        zero.IsFailure.Should().BeTrue();
        zero.Error.Code.Should().Be("transfer.line_quantity_invalid");
    }

    [Fact]
    public void Create_DuplicateLines_AreRejected()
    {
        Result<Transfer> result = Transfer.Create(
            Warehouse,
            Store,
            [Spec(Coke, 5m), Spec(Coke, 5m)],
            Requester,
            Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("transfer.duplicate_line");
    }

    [Fact]
    public void ApprovalFlow_SubmitReviewApprove_WithAmendments()
    {
        Result<Transfer> result = Transfer.Create(
            Warehouse,
            Store,
            [Spec(Coke, 10m)],
            Requester,
            Now);
        Transfer transfer = result.Value;

        transfer.Submit(Requester, Now).IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(TransferStatus.Submitted);

        transfer.Review(Approver, Now, "checking stock").IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(TransferStatus.InReview);

        transfer.Approve(Approver, Now, [new TransferLineAmendment(1, 8m)], "taking less").IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(TransferStatus.Approved);
        transfer.ApprovedByUserId.Should().Be(Approver);
        transfer.Lines[0].RequestedQuantity.Should().Be(8m);
        transfer.CustodyEvents.Select(e => e.Kind).Should().Equal(
            TransferCustodyEventKind.Created,
            TransferCustodyEventKind.Submitted,
            TransferCustodyEventKind.Reviewed,
            TransferCustodyEventKind.ApprovedWithAmendment);
    }

    [Fact]
    public void Approve_WithoutAmendments_RecordsPlainApproval()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;

        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Approve(Approver, Now).IsSuccess.Should().BeTrue();

        transfer.CustodyEvents[^1].Kind.Should().Be(TransferCustodyEventKind.Approved);
        transfer.EstimatedTotalValue(new Dictionary<ProductId, decimal> { [Coke] = 100m })
            .Should().Be(1000m);
    }

    [Fact]
    public void Approve_InvalidAmendment_IsRejected()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;

        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);

        Result approve = transfer.Approve(Approver, Now, [new TransferLineAmendment(99, 5m)]);
        approve.IsFailure.Should().BeTrue();
        approve.Error.Code.Should().Be("transfer.amendment_invalid");
        transfer.Status.Should().Be(TransferStatus.InReview);
    }

    [Fact]
    public void Reject_ReturnsToDraft_FromSubmittedOrInReview()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;

        transfer.Submit(Requester, Now);
        transfer.Reject(Approver, Now, "not enough budget").IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(TransferStatus.Draft);

        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Reject(Approver, Now, null).IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(TransferStatus.Draft);
        transfer.CustodyEvents.Select(e => e.Kind).Should().Contain(TransferCustodyEventKind.Rejected);
    }

    [Fact]
    public void WrongStateTransitions_AreRejected()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;

        transfer.Review(Requester, Now, null).Error.Code.Should().Be("transfer.invalid_state");
        transfer.Approve(Approver, Now).Error.Code.Should().Be("transfer.invalid_state");

        transfer.Submit(Requester, Now);
        transfer.Submit(Requester, Now).Error.Code.Should().Be("transfer.invalid_state");
    }

    [Fact]
    public void Pick_RecordsAllocationsAndMovesToPicking_EnforcingRequestedCap()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Approve(Approver, Now);

        Result pick = transfer.Pick(
            Picker,
            Now,
            [
                new TransferPickAllocationSpec(1, BatchA, 6m, 95m),
                new TransferPickAllocationSpec(1, BatchB, 4m, 97m),
            ]);

        pick.IsSuccess.Should().BeTrue(because: string.Join("; ", pick.Errors.Select(e => e.Code)));
        transfer.Status.Should().Be(TransferStatus.Picking);
        transfer.PickedByUserId.Should().Be(Picker);
        transfer.Allocations.Should().HaveCount(2);
        transfer.Allocations.Should().Contain(a => a.BatchId == BatchA && a.Quantity == 6m && a.UnitCost == 95m);
        transfer.Allocations.Sum(a => a.LineTotal).Should().Be(958m);
        transfer.CustodyEvents.Select(e => e.Kind).Should().Contain(TransferCustodyEventKind.Picked);
    }

    [Fact]
    public void Pick_RefusesBeforeApproval()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;

        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, null, 10m, 90m)])
            .Error.Code.Should().Be("transfer.invalid_state");
    }

    [Fact]
    public void Pick_ValidatesLinesQuantitiesAndDuplicates()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Approve(Approver, Now);

        transfer.Pick(Picker, Now, []).Error.Code.Should().Be("transfer.nothing_picked");
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(99, null, 1m, 1m)])
            .Error.Code.Should().Be("transfer.line_unknown");
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, null, 0m, 1m)])
            .Error.Code.Should().Be("transfer.pick_quantity_invalid");
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, null, 11m, 1m)])
            .Error.Code.Should().Be("transfer.pick_exceeds_requested");
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, BatchA, 5m, 1m), new TransferPickAllocationSpec(1, BatchA, 5m, 1m)])
            .Error.Code.Should().Be("transfer.duplicate_allocation");
    }

    [Fact]
    public void Ready_RequiresPicking()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;

        transfer.Ready(Now).Error.Code.Should().Be("transfer.invalid_state");

        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Approve(Approver, Now);
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, BatchA, 10m, 90m)]);

        transfer.Ready(Now).IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(TransferStatus.Ready);
    }

    [Fact]
    public void Dispatch_AssignsNumbersAndMovesToDispatched()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Approve(Approver, Now);
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, BatchA, 10m, 90m)]);
        transfer.Ready(Now);

        Result dispatch = transfer.Dispatch(
            Now,
            TrfNumber,
            TransferShipmentId.New(),
            ShpNumber.Value,
            Picker);

        dispatch.IsSuccess.Should().BeTrue(because: string.Join("; ", dispatch.Errors.Select(e => e.Code)));
        transfer.Status.Should().Be(TransferStatus.Dispatched);
        transfer.Number.Should().Be(TrfNumber.Value);
        transfer.ShipmentNumber.Should().Be(ShpNumber.Value);
        transfer.DispatchedByUserId.Should().Be(Picker);
        transfer.CustodyEvents.Select(e => e.Kind).Should().Contain(TransferCustodyEventKind.Dispatched);
    }

    [Fact]
    public void Dispatch_RefusesBeforeReady_AndRefusesTwice()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;

        transfer.Dispatch(Now, TrfNumber, TransferShipmentId.New(), ShpNumber.Value, Picker)
            .Error.Code.Should().Be("transfer.invalid_state");

        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Approve(Approver, Now);
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, BatchA, 10m, 90m)]);
        transfer.Ready(Now);

        transfer.Dispatch(Now, TrfNumber, TransferShipmentId.New(), ShpNumber.Value, Picker).IsSuccess.Should().BeTrue();
        transfer.Dispatch(Now, TrfNumber, TransferShipmentId.New(), ShpNumber.Value, Picker)
            .Error.Code.Should().Be("transfer.already_numbered");
    }

    [Fact]
    public void CancelDispatch_ReturnsToCancelled_OnlyWhenNothingArrived()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);

        transfer.CancelDispatch(Now, string.Empty, Requester).Error.Code.Should().Be("transfer.cancel_reason_required");

        Result cancel = transfer.CancelDispatch(Now, "no truck", Requester);
        cancel.IsSuccess.Should().BeTrue(because: string.Join("; ", cancel.Errors.Select(e => e.Code)));
        transfer.Status.Should().Be(TransferStatus.Cancelled);
        transfer.CancelReason.Should().Be("no truck");
        transfer.CancelledByUserId.Should().Be(Requester);
        transfer.CustodyEvents.Select(e => e.Kind).Should().Contain(TransferCustodyEventKind.DispatchCancelled);
    }

    [Fact]
    public void CancelDispatch_RefusesAfterArrival()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);

        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(1, BatchA, 10m, 0m)])
            .IsSuccess.Should().BeTrue();

        transfer.CancelDispatch(Now, "too late", Requester).Error.Code.Should().Be("transfer.cancel_after_receive");
    }

    [Fact]
    public void Receive_InFull_ClosesTheArrivalWithoutDiscrepancy()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);

        Result receive = transfer.Receive(
            Receiver,
            Now,
            TransferReceiptId.New(),
            TrcNumber.Value,
            [Received(1, BatchA, 10m, 0m)]);

        receive.IsSuccess.Should().BeTrue(because: string.Join("; ", receive.Errors.Select(e => e.Code)));
        transfer.Status.Should().Be(TransferStatus.Received);
        transfer.ReceiptId.Should().NotBeNull();
        transfer.ReceiptNumber.Should().Be(TrcNumber.Value);
        transfer.Allocations[0].FullyAccounted.Should().BeTrue();
        transfer.Discrepancies.Should().BeEmpty();
        transfer.CustodyEvents.Select(e => e.Kind).Should().Contain(TransferCustodyEventKind.Received);
    }

    [Fact]
    public void Receive_PartialArrival_RecordsShortfallAsDiscrepancy()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);

        Result receive = transfer.Receive(
            Receiver,
            Now,
            TransferReceiptId.New(),
            TrcNumber.Value,
            [Received(1, BatchA, 6m, 2m)]);

        receive.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(TransferStatus.PartiallyReceived);
        transfer.Allocations[0].Shortfall.Should().Be(2m);
        transfer.Allocations[0].FullyAccounted.Should().BeFalse();
        transfer.Discrepancies.Should().ContainSingle();
        transfer.Discrepancies[0].Kind.Should().Be(TransferDiscrepancyKind.Shortage);
        transfer.Discrepancies[0].Quantity.Should().Be(2m);
        transfer.Discrepancies[0].BatchId.Should().Be(BatchA);
    }

    [Fact]
    public void Receive_RefusesOverReceiptAndUnknownAllocations()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);

        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [])
            .Error.Code.Should().Be("transfer.nothing_received");
        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(99, BatchA, 1m, 0m)])
            .Error.Code.Should().Be("transfer.receive_unknown_allocation");
        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(1, BatchA, 11m, 0m)])
            .Error.Code.Should().Be("transfer.receive_exceeds_allocation");
        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(1, BatchA, -1m, 0m)])
            .Error.Code.Should().Be("transfer.receive_quantity_invalid");
    }

    [Fact]
    public void ResolveDiscrepancy_RecordOutcomeAndResolver()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);
        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(1, BatchA, 8m, 0m)]);

        TransferDiscrepancy discrepancy = transfer.Discrepancies[0];

        Result resolve = transfer.ResolveDiscrepancy(
            discrepancy.Id,
            TransferDiscrepancyResolutionOutcome.Found,
            Approver,
            Now,
            "found under the counter");

        resolve.IsSuccess.Should().BeTrue(because: string.Join("; ", resolve.Errors.Select(e => e.Code)));
        discrepancy.IsResolved.Should().BeTrue();
        discrepancy.ResolutionOutcome.Should().Be(TransferDiscrepancyResolutionOutcome.Found);
        discrepancy.ResolvedByUserId.Should().Be(Approver);
        discrepancy.ResolutionNote.Should().Be("found under the counter");
        transfer.CustodyEvents.Select(e => e.Kind).Should().Contain(TransferCustodyEventKind.Resolved);

        transfer.ResolveDiscrepancy(discrepancy.Id, TransferDiscrepancyResolutionOutcome.WriteOff, Approver, Now, null)
            .Error.Code.Should().Be("transfer.discrepancy_already_resolved");
    }

    [Fact]
    public void ResolveDiscrepancy_UnknownOrAlreadyResolved_IsRejected()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);
        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(1, BatchA, 8m, 0m)]);

        transfer.ResolveDiscrepancy(TransferDiscrepancyId.New(), TransferDiscrepancyResolutionOutcome.Found, Approver, Now, null)
            .Error.Code.Should().Be("transfer.discrepancy_unknown");
    }

    [Fact]
    public void Verify_ClosesOnlyOnceEverythingIsResolved()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);
        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(1, BatchA, 8m, 0m)]);

        transfer.Verify(Receiver, Now).Error.Code.Should().Be("transfer.verify_has_open_variance");

        TransferDiscrepancy discrepancy = transfer.Discrepancies[0];
        transfer.ResolveDiscrepancy(discrepancy.Id, TransferDiscrepancyResolutionOutcome.WriteOff, Approver, Now, null);

        Result verify = transfer.Verify(Receiver, Now);
        verify.IsSuccess.Should().BeTrue(because: string.Join("; ", verify.Errors.Select(e => e.Code)));
        transfer.Status.Should().Be(TransferStatus.Closed);
        transfer.VerifiedByUserId.Should().Be(Receiver);
        transfer.CustodyEvents.Select(e => e.Kind).Should().Contain(TransferCustodyEventKind.Verified);

        transfer.Verify(Receiver, Now).Error.Code.Should().Be("transfer.invalid_state");
    }

    [Fact]
    public void Verify_NothingArrived_IsRefused()
    {
        Result<Transfer> result = Transfer.Create(Warehouse, Store, [Spec(Coke, 10m)], Requester, Now);
        Transfer transfer = result.Value;
        Dispatched(transfer);
        transfer.Receive(Receiver, Now, TransferReceiptId.New(), TrcNumber.Value, [Received(1, BatchA, 0m, 0m)]);
        transfer.ResolveDiscrepancy(transfer.Discrepancies[0].Id, TransferDiscrepancyResolutionOutcome.WriteOff, Approver, Now, null);

        transfer.Verify(Receiver, Now).Error.Code.Should().Be("transfer.verify_nothing_received");
    }

    private static void Dispatched(Transfer transfer)
    {
        transfer.Submit(Requester, Now);
        transfer.Review(Approver, Now, null);
        transfer.Approve(Approver, Now);
        transfer.Pick(Picker, Now, [new TransferPickAllocationSpec(1, BatchA, 10m, 90m)]);
        transfer.Ready(Now);
        transfer.Dispatch(Now, TrfNumber, TransferShipmentId.New(), ShpNumber.Value, Picker);
    }

    private static TransferLineSpec Spec(ProductId productId, decimal quantity, string? note = null)
        => new(productId, quantity, note);

    private static TransferReceiveAllocationSpec Received(int lineNo, BatchId? batchId, decimal received, decimal damaged)
        => new(lineNo, batchId, received, damaged);
}