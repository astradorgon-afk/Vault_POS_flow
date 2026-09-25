using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Sync;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// Trading at a register while head office cannot be reached: the shift the
/// till trades under, the cash sale it records and queues, and the upload that
/// delivers the queue once the connection is back.
/// </summary>
public sealed class DeviceOfflineTradingTests
{
    [Fact]
    public async Task ACashSale_IsRecordedAndQueued_InOneTransaction_UnderTheRegistersOwnNumber()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        Result<DeviceLocalSale> sale = await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos");

        sale.IsSuccess.Should().BeTrue(sale.IsFailure ? sale.Error.ToString() : string.Empty);
        sale.Value.Number.Should().Be("SAL-2026-D03-000001");
        sale.Value.NetTotal.Should().Be(103m);
        sale.Value.ReadPayments().Single().Change.Should().Be(7m);
        sale.Value.ReadLines().Select(l => l.Name).Should().Equal("Rice 1kg", "Soap");

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        List<OutboxEvent> queued = await context.Outbox.AsNoTracking().OrderBy(e => e.DeviceSequence).ToListAsync();
        queued.Select(e => e.Type).Should().Equal(SyncEventType.ShiftOpened, SyncEventType.SaleCompleted);

        OutboxEvent saleEvent = queued[1];
        saleEvent.EventId.Should().Be(sale.Value.EventId, "the event is the sale's idempotency key at head office");
        saleEvent.UserId.Should().Be(host.CashierId);

        // What head office replays: the same command input the online path sends.
        SaleSyncPayload payload = JsonSerializer.Deserialize<SaleSyncPayload>(saleEvent.PayloadJson)!;
        payload.Number.Should().Be("SAL-2026-D03-000001");
        payload.CashierShiftId.Should().Be(shift.Value);
        payload.CashierId.Should().Be(host.CashierId.Value);
        payload.DeviceId.Should().Be(host.DeviceId.Value);
        payload.Lines.Should().HaveCount(2);
        payload.Lines.Should().OnlyContain(l => l.UnitPriceOverride == null && l.Discount == 0m);
        payload.Payments.Single().Should().Be(new SaleSyncPayment(PaymentMethod.Cash, 103m, 110m, null));

        (await context.LocalAudit.AnyAsync(a => a.Action == AuditActions.Sales.SaleCompleted))
            .Should().BeTrue("the register is the only witness to an offline sale until it syncs");
    }

    [Fact]
    public async Task SalesAreNumberedInSequence()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos");
        Result<DeviceLocalSale> second = await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos");

        second.Value.Number.Should().Be("SAL-2026-D03-000002");
        (await host.Sales.FindAsync("SAL-2026-D03-000002"))!.EventId.Should().Be(second.Value.EventId);
    }

    [Theory]
    [InlineData(PaymentMethod.Card)]
    [InlineData(PaymentMethod.EWallet)]
    public async Task OnlyCash_IsTakenOffline(PaymentMethod method)
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        Result<DeviceLocalSale> sale = await host.Sales.RecordAsync(
            OfflineRegisterHost.CashSale(shift, method: method, tendered: null), "Maria Santos");

        sale.Error.Should().Be(OfflineSaleErrors.CashOnly);
        await AssertOnlyTheShiftIsQueuedAsync(host);
    }

    [Fact]
    public async Task APaymentThatDoesNotSettleTheSale_IsRefused()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        (await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift, amount: 100m), "Maria Santos")).Error
            .Should().Be(OfflineSaleErrors.PaymentMismatch);
        (await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift, tendered: 100m), "Maria Santos")).Error
            .Should().Be(OfflineSaleErrors.TenderShort);
        await AssertOnlyTheShiftIsQueuedAsync(host);
    }

    [Fact]
    public async Task WithoutSaleCreateInTheSnapshot_NothingIsSold()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync(
            cashierPermissions: [Permissions.Sales.OpenShift]);
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        (await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos")).Error
            .Should().Be(OfflineSaleErrors.NotAuthorized);
        await AssertOnlyTheShiftIsQueuedAsync(host);
    }

    [Fact]
    public async Task OnceTheSnapshotExpires_TheTillStopsSelling_EvenWithSomeoneSignedIn()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        host.Database.Clock.UtcNow = TemporaryDeviceDatabase.Now.AddHours(73);

        (await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos")).Error
            .Should().Be(OfflineSaleErrors.NotAuthorized);
    }

    [Fact]
    public async Task ACashierCannotSellIntoSomeoneElsesShift()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        host.SignInAs(host.OtherCashierId);

        (await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Other Cashier")).Error
            .Should().Be(OfflineSaleErrors.ShiftNotOpenHere, "head office would refuse it for the same reason");
    }

    [Fact]
    public async Task WithoutAnOpenShift_NothingIsSold()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);

        (await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(CashierShiftId.New()), "Maria Santos")).Error
            .Should().Be(OfflineSaleErrors.ShiftNotOpenHere);
    }

    [Fact]
    public async Task ASaleFirstSentOnline_IsQueuedUnderItsOriginalNumberAndIdentity_AndOnlyOnce()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();
        Guid eventId = Guid.CreateVersion7();

        OfflineSaleRequest retry = OfflineRegisterHost.CashSale(shift, number: "SAL-2026-D03-000041", eventId: eventId);
        Result<DeviceLocalSale> first = await host.Sales.RecordAsync(retry, "Maria Santos");
        Result<DeviceLocalSale> again = await host.Sales.RecordAsync(retry, "Maria Santos");

        first.Value.Number.Should().Be("SAL-2026-D03-000041");
        first.Value.EventId.Value.Should().Be(eventId);
        again.Value.EventId.Should().Be(first.Value.EventId);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.Outbox.CountAsync(e => e.Type == SyncEventType.SaleCompleted)).Should().Be(1);
        (await context.Outbox.SingleAsync(e => e.Type == SyncEventType.SaleCompleted)).EventId.Value.Should().Be(eventId);
    }

    [Fact]
    public async Task ANumberAnotherRegisterIssued_IsRefused()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        (await host.Sales.RecordAsync(
                OfflineRegisterHost.CashSale(shift, number: "SAL-2026-D07-000001", eventId: Guid.CreateVersion7()),
                "Maria Santos")).Error
            .Should().Be(OfflineSaleErrors.NumberInvalid);
    }

    [Fact]
    public async Task TheTillContext_ComesFromTheStoreDataAndTheOpenShift()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();

        DeviceTillContext before = (await host.Shifts.GetTillContextAsync())!;
        before.OpenShift.Should().BeNull();
        before.CashRoundingIncrement.Should().Be(0.25m, "the store's own setting travels in the store data");
        before.Currency.Should().Be("PHP");
        before.BusinessDate.Should().Be(new DateOnly(2026, 9, 17));

        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();

        DeviceKnownShift open = (await host.Shifts.GetTillContextAsync())!.OpenShift!;
        open.ShiftId.Should().Be(shift.Value);
        open.CashierUserId.Should().Be(host.CashierId.Value);
        open.CashierName.Should().Be("Maria Santos");
        open.OpeningFloat.Should().Be(1000m);
    }

    [Fact]
    public async Task AShiftHeadOfficeOpened_IsMirrored_SoTheTillKeepsTradingInItOffline()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        DeviceKnownShift fromHeadOffice = new(
            Guid.CreateVersion7(), "SHF-2026-D03-0007", host.CashierId.Value, "Maria Santos",
            new DateOnly(2026, 9, 17), 500m, TemporaryDeviceDatabase.Now.AddHours(-1));

        await host.Shifts.ReconcileAsync(fromHeadOffice);

        (await host.Shifts.GetTillContextAsync())!.OpenShift!.ShiftId.Should().Be(fromHeadOffice.ShiftId);

        host.SignInAs(host.CashierId);
        Result<DeviceLocalSale> sale = await host.Sales.RecordAsync(
            OfflineRegisterHost.CashSale(new CashierShiftId(fromHeadOffice.ShiftId)), "Maria Santos");
        sale.IsSuccess.Should().BeTrue(sale.IsFailure ? sale.Error.ToString() : string.Empty);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.Outbox.Select(e => e.Type).ToListAsync())
            .Should().Equal([SyncEventType.SaleCompleted], "head office already holds the mirrored shift");
    }

    [Fact]
    public async Task AMirroredShift_StopsASecondShiftOpeningOffline()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        await host.Shifts.ReconcileAsync(new DeviceKnownShift(
            Guid.CreateVersion7(), "SHF-2026-D03-0007", host.OtherCashierId.Value, "Other Cashier",
            new DateOnly(2026, 9, 17), 500m, TemporaryDeviceDatabase.Now.AddHours(-1)));

        host.SignInAs(host.CashierId);
        Func<Task> open = host.OpenShiftAsync;

        await open.Should().ThrowAsync<Exception>("one drawer, one open shift, whoever opened it");
    }

    [Fact]
    public async Task ShiftsHeadOfficeClosed_AreDropped_OnceEverythingHasBeenDelivered()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        await host.OpenShiftAsync();

        // Head office has not seen the shift yet: its "nothing open" is behind.
        await host.Shifts.ReconcileAsync(null);
        (await host.Shifts.GetTillContextAsync())!.OpenShift.Should().NotBeNull();

        await host.Uploader.UploadAsync(host.CashierId, AcceptAll);
        await host.Shifts.ReconcileAsync(null);

        (await host.Shifts.GetTillContextAsync())!.OpenShift.Should().BeNull("head office closed it");
    }

    [Fact]
    public async Task TheUpload_DeliversTheSignedInPersonsEventsInOrder_AndMarksThemSynchronized()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();
        await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos");

        List<SyncPushRequest> sent = [];
        DeviceUploadResult result = await host.Uploader.UploadAsync(host.CashierId, (request, token) =>
        {
            sent.Add(request);
            return AcceptAll(request, token);
        });

        result.Delivered.Should().Be(2);
        result.Remaining.Should().Be(0);
        sent.Should().ContainSingle();
        sent[0].DeviceId.Should().Be(host.DeviceId.Value);
        sent[0].Events.Select(e => e.EventType).Should().Equal("ShiftOpened", "SaleCompleted");
        sent[0].Events.Select(e => e.DeviceSequence).Should().Equal(1, 2);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.Outbox.AllAsync(e => e.Status == OutboxStatus.Synchronized)).Should().BeTrue();
        (await context.Outbox.AllAsync(e => e.AttemptCount == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task TheUpload_StopsAtSomeoneElsesEvent_AndSaysWhoItWaitsFor()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.OtherCashierId);
        await host.OpenShiftAsync();
        host.SignInAs(host.CashierId);

        List<SyncPushRequest> sent = [];
        DeviceUploadResult result = await host.Uploader.UploadAsync(host.CashierId, (request, token) =>
        {
            sent.Add(request);
            return AcceptAll(request, token);
        });

        sent.Should().BeEmpty("head office only takes a person's events in their own session, in order");
        result.Remaining.Should().Be(1);
        result.WaitingForOthers.Should().Be(1);
        result.WaitingFor.Should().Equal("Other Cashier");
    }

    [Fact]
    public async Task AFailedTransport_PutsTheBatchBack_AndLosesNothing()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        await host.OpenShiftAsync();

        Func<Task> upload = () => host.Uploader.UploadAsync(
            host.CashierId, (_, _) => throw new HttpRequestException("connection refused"));

        await upload.Should().ThrowAsync<HttpRequestException>();

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        OutboxEvent queued = await context.Outbox.SingleAsync();
        queued.Status.Should().Be(OutboxStatus.Pending);
        queued.LastError.Should().Be("connection refused");
        (await host.Uploader.GetBacklogAsync(host.CashierId)).Remaining.Should().Be(1);
    }

    [Fact]
    public async Task HeadOfficesAnswers_AreRecordedPerEvent()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();
        await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos");
        await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos");

        DeviceUploadResult result = await host.Uploader.UploadAsync(host.CashierId, (request, _) =>
            Task.FromResult(new SyncPushResponse(
                TemporaryDeviceDatabase.Now,
                [
                    SyncPushEventResult.Accepted(request.Events[0].EventId, TemporaryDeviceDatabase.Now),
                    SyncPushEventResult.RequiresReview(
                        request.Events[1].EventId, TemporaryDeviceDatabase.Now, "sale.stock_insufficient", "Not enough stock."),
                    SyncPushEventResult.Rejected(request.Events[2].EventId, "sale.product_unknown", "Unknown product.", "QuarantineAndReview"),
                ],
                3,
                null,
                null)));

        result.Delivered.Should().Be(1);
        result.Flagged.Should().Be(2);
        result.Remaining.Should().Be(0, "head office holds every one of them now, on its sync-failure queue if not accepted");

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        List<OutboxStatus> statuses = await context.Outbox.OrderBy(e => e.DeviceSequence).Select(e => e.Status).ToListAsync();
        statuses.Should().Equal(OutboxStatus.Synchronized, OutboxStatus.RequiresReview, OutboxStatus.RequiresReview);
        (await context.Outbox.OrderBy(e => e.DeviceSequence).LastAsync()).LastError.Should().Be("Unknown product.");
    }

    [Fact]
    public async Task ADeferral_LeavesTheEventQueued_AndEndsTheUpload()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        await host.OpenShiftAsync();

        int calls = 0;
        DeviceUploadResult result = await host.Uploader.UploadAsync(host.CashierId, (request, _) =>
        {
            calls++;
            return Task.FromResult(new SyncPushResponse(
                TemporaryDeviceDatabase.Now,
                [SyncPushEventResult.Deferred(request.Events[0].EventId, "sync.sequence_gap", "Waiting.")],
                0,
                null,
                null));
        });

        calls.Should().Be(1, "asking again at once would get the same answer");
        result.Remaining.Should().Be(1);
    }

    [Fact]
    public async Task ALongQueue_GoesUpInBatchesOfAHundred()
    {
        await using OfflineRegisterHost host = await OfflineRegisterHost.StartAsync();
        host.SignInAs(host.CashierId);
        CashierShiftId shift = await host.OpenShiftAsync();
        for (int i = 0; i < 120; i++)
        {
            (await host.Sales.RecordAsync(OfflineRegisterHost.CashSale(shift), "Maria Santos")).IsSuccess.Should().BeTrue();
        }

        List<int> batches = [];
        DeviceUploadResult result = await host.Uploader.UploadAsync(host.CashierId, (request, token) =>
        {
            batches.Add(request.Events.Count);
            return AcceptAll(request, token);
        });

        batches.Should().Equal(100, 21);
        result.Delivered.Should().Be(121);
        result.Remaining.Should().Be(0);
    }

    private static Task<SyncPushResponse> AcceptAll(SyncPushRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SyncPushResponse(
            TemporaryDeviceDatabase.Now,
            [.. request.Events.Select(e => SyncPushEventResult.Accepted(e.EventId, TemporaryDeviceDatabase.Now))],
            request.Events.Max(e => e.DeviceSequence),
            null,
            null));

    private static async Task AssertOnlyTheShiftIsQueuedAsync(OfflineRegisterHost host)
    {
        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.Outbox.Select(e => e.Type).ToListAsync()).Should().Equal(SyncEventType.ShiftOpened);
        (await context.LocalSales.AnyAsync()).Should().BeFalse("a refused sale leaves nothing behind");
    }
}
