using FluentAssertions;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Quarantine;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Sales;

namespace Pos.Application.Tests.Sales;

/// <summary>Checks failure handling after the disposition ledger group has been staged.</summary>
public sealed class DisposeSalesReturnCommandHandlerTests
{
    [Fact]
    public async Task ConcurrentLineChange_ReturnsConflictSoThePipelineRollsBackTheLedger()
    {
        LocationId store = LocationId.New();
        UserId actor = UserId.New();
        DateTimeOffset now = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        DateOnly date = new(2026, 9, 16);
        SalesReturn returned = SalesReturn.CreateBlind(
            DocumentNumber.CreateForDevice(DocumentType.SalesReturn, 2026, "D01", 1),
            EventId.New(), store, CashierShiftId.New(), DeviceId.New(), null, date, now, actor,
            "Receipt unavailable", [new BlindReturnItemSpec(ProductId.New(), "Widget", null, 1m,
                UnitOfMeasureId.New(), 100m, null, true, false, 30m)]).Value;
        IReturnDispositionRepository repository = Substitute.For<IReturnDispositionRepository>();
        IQuarantineRepository quarantine = Substitute.For<IQuarantineRepository>();
        IInventoryLedger ledger = Substitute.For<IInventoryLedger>();
        IAuditWriter audit = Substitute.For<IAuditWriter>();
        ICurrentUser user = Substitute.For<ICurrentUser>();
        ISystemClock clock = Substitute.For<ISystemClock>();
        user.UserId.Returns(actor);
        clock.UtcNow.Returns(now);
        clock.BusinessDateFor("Asia/Manila").Returns(date);
        repository.GetReturnAsync(returned.Id, Arg.Any<CancellationToken>()).Returns(returned);
        quarantine.GetLocationInfoAsync(store, Arg.Any<CancellationToken>())
            .Returns(new QuarantineLocationInfo(LocationKind.Store, "Asia/Manila"));
        EventId eventId = EventId.New();
        ledger.PostAsync(Arg.Any<MovementGroupSpec>(), Arg.Any<CancellationToken>())
            .Returns(Result<PostedMovementGroup>.Success(new PostedMovementGroup(MovementGroupId.New(), eventId, 2, false, now)));
        repository.AddAsync(Arg.Any<SalesReturnDisposition>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ConcurrencyConflictException(new InvalidOperationException("Stale return line"))));
        DisposeSalesReturnCommandHandler handler = new(repository, quarantine,
            Substitute.For<IDocumentNumberGenerator>(), ledger, audit, user, clock);

        Result<EventId> result = await handler.HandleAsync(new DisposeSalesReturnCommand(eventId,
            returned.Id, store, 1, 1m, ReturnDispositionKind.Restock, AdjustmentReasonCode.CountCorrection,
            "Inspected"), CancellationToken.None);

        result.Error.Should().Be(ReturnDispositionErrors.Contention);
        await audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }
}
