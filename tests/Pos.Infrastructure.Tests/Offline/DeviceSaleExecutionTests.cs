using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Common.Offline;
using Pos.Application.Identity;
using Pos.Application.Inventory;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// A cash sale rung up with no network, through the real container: the same
/// handler the server runs, numbered by the device, authorized by a cached
/// snapshot, posted to the device's own ledger and queued for upload — all in
/// one transaction that either happened or did not.
/// </summary>
public sealed class DeviceSaleExecutionTests
{
    private static readonly DateTimeOffset Now = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task ADeviceSellsOffline_PostsTheLedger_AndQueuesTheSale()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        Result<SaleId> sold = await host.SellAsync(quantity: 3m, unitPaid: 75m);

        sold.IsSuccess.Should().BeTrue(sold.IsFailure ? sold.Error.ToString() : string.Empty);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();

        Sale sale = await context.LocalSales
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .SingleAsync(CancellationToken.None);

        sale.Number.Should().Be("SAL-2026-D03-000001", "the device numbered it itself");
        sale.Items.Should().ContainSingle();
        sale.Payments.Should().ContainSingle();

        // Stock left the shelf and went to the customer: the ledger stays closed.
        (await host.QuantityAsync(host.StoreId, InventoryState.Available)).Should().Be(17m);
        (await host.QuantityAsync(host.CustomerLocationId, InventoryState.External)).Should().Be(3m);

        OutboxEvent queued = await context.Outbox
            .Where(e => e.Type == SyncEventType.SaleCompleted)
            .SingleAsync(CancellationToken.None);
        queued.PayloadJson.Should().Contain("SAL-2026-D03-000001");
    }

    [Fact]
    public async Task SellingMoreThanTheRegisterHolds_IsRefused_AndLeavesNothingBehind()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(2m);

        Result<SaleId> sold = await host.SellAsync(quantity: 5m, unitPaid: 125m);

        sold.IsFailure.Should().BeTrue("an outage does not relax the negative-stock policy");

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.LocalSales.AnyAsync(CancellationToken.None)).Should().BeFalse();
        (await context.Outbox.AnyAsync(e => e.Type == SyncEventType.SaleCompleted, CancellationToken.None))
            .Should().BeFalse("a sale that did not happen must never reach head office");
        (await host.QuantityAsync(host.StoreId, InventoryState.Available)).Should().Be(2m);
    }

    [Fact]
    public async Task WithoutSaleCreateInTheSnapshot_NothingIsSold()
    {
        await using SaleHost host = await SaleHost.StartAsync(grantSalePermission: false);
        await host.ReceiveStockAsync(20m);

        Result<SaleId> sold = await host.SellAsync(quantity: 1m, unitPaid: 25m);

        sold.IsFailure.Should().BeTrue();
        sold.Error.Code.Should().Be("auth.permission_denied");
    }

    [Fact]
    public async Task ANamedCustomerIsRefusedOffline_WhileAnAnonymousCashSaleIsNot()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        Result<SaleId> named = await host.SellAsync(quantity: 1m, unitPaid: 25m, customerId: CustomerId.New());

        named.IsFailure.Should().BeTrue(
            "the device caches no customers, and dropping the account silently would lose the credit");

        Result<SaleId> anonymous = await host.SellAsync(quantity: 1m, unitPaid: 25m);

        anonymous.IsSuccess.Should().BeTrue(anonymous.IsFailure ? anonymous.Error.ToString() : string.Empty);
    }

    [Fact]
    public async Task TheSameSaleNumberCannotBeRungUpTwice()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        EventId eventId = EventId.New();
        DocumentNumber number = await host.AllocateSaleNumberAsync();

        (await host.SellAsync(3m, 75m, eventId: eventId, number: number)).IsSuccess.Should().BeTrue();

        // A retry of an *upload* is what the event identifier makes safe; ringing
        // the same numbered sale up again on the till is not a retry, it is a
        // second sale wearing the first one's receipt number. The unique index
        // stops it at the database rather than letting the drawer disagree with
        // the ledger.
        Func<Task> second = () => host.SellAsync(3m, 75m, eventId: eventId, number: number);

        DbUpdateException refused = (await second.Should().ThrowAsync<DbUpdateException>()).Which;
        refused.InnerException.Should().BeOfType<SqliteException>()
            .Which.Message.Should().Contain("local_sale.number");

        (await host.QuantityAsync(host.StoreId, InventoryState.Available))
            .Should().Be(17m, "the refused second sale drew nothing");
    }

    [Fact]
    public async Task ClosingTheShift_ReconcilesTheDrawerAgainstWhatWasSoldForCash()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        (await host.SellAsync(quantity: 3m, unitPaid: 75m)).IsSuccess.Should().BeTrue();
        (await host.SellAsync(quantity: 1m, unitPaid: 25m)).IsSuccess.Should().BeTrue();

        // Opening float 2000 plus 100 taken in cash: a drawer counted at 2100
        // balances, and the device worked that out from its own sales.
        Result<CashierShiftId> closed = await host.CloseShiftAsync(declared: 2100m, counted: 2100m);

        closed.IsSuccess.Should().BeTrue(closed.IsFailure ? closed.Error.ToString() : string.Empty);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        CashierShift shift = await context.LocalShifts.SingleAsync(CancellationToken.None);

        shift.Status.Should().Be(ShiftStatus.Closed);
        shift.CountedCash.Should().Be(2100m);
        shift.CashVariance.Should().Be(0m, "the float and the cash sales account for the drawer");
    }

    [Fact]
    public async Task AShortDrawerClosesWithTheVarianceRecorded()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        (await host.SellAsync(quantity: 3m, unitPaid: 75m)).IsSuccess.Should().BeTrue();

        Result<CashierShiftId> closed = await host.CloseShiftAsync(declared: 2070m, counted: 2070m);

        closed.IsSuccess.Should().BeTrue(closed.IsFailure ? closed.Error.ToString() : string.Empty);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        CashierShift shift = await context.LocalShifts.SingleAsync(CancellationToken.None);

        shift.CashVariance.Should().Be(-5m, "the shortfall is recorded, not hidden");
    }

    [Fact]
    public async Task VoidingASale_ReturnsTheStock_AndQueuesTheVoid()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        Result<SaleId> sold = await host.SellAsync(quantity: 3m, unitPaid: 75m);
        sold.IsSuccess.Should().BeTrue(sold.IsFailure ? sold.Error.ToString() : string.Empty);

        Result<SaleId> voided = await host.SendCommandAsync(
            new VoidSaleCommand(
                EventId.New(),
                sold.Value,
                host.StoreId,
                host.ShiftId,
                host.DeviceId,
                DateOnly.FromDateTime(Now.UtcDateTime),
                host.CashierId,
                Now,
                "wrong item scanned"));

        voided.IsSuccess.Should().BeTrue(voided.IsFailure ? voided.Error.ToString() : string.Empty);

        // The goods come back to the shelf by a reversing post, not by editing
        // history: the ledger is append-only on a device too.
        (await host.QuantityAsync(host.StoreId, InventoryState.Available)).Should().Be(20m);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        Sale sale = await context.LocalSales.SingleAsync(CancellationToken.None);
        sale.Status.Should().Be(SaleStatus.Voided);
        sale.VoidReason.Should().Be("wrong item scanned");

        (await context.Outbox.CountAsync(e => e.Type == SyncEventType.SaleVoided, CancellationToken.None))
            .Should().Be(1);
    }

    [Fact]
    public async Task ReprintingAReceipt_IsRecordedAndReported()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        Result<SaleId> sold = await host.SellAsync(quantity: 3m, unitPaid: 75m);
        sold.IsSuccess.Should().BeTrue(sold.IsFailure ? sold.Error.ToString() : string.Empty);

        Result<SaleId> reprinted = await host.SendCommandAsync(
            new ReprintSaleReceiptCommand(
                sold.Value,
                host.StoreId,
                host.DeviceId,
                "customer lost the copy",
                Now,
                host.CashierId));

        reprinted.IsSuccess.Should().BeTrue(reprinted.IsFailure ? reprinted.Error.ToString() : string.Empty);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();

        SaleReceiptPrint print = await context.LocalReceiptPrints.SingleAsync(CancellationToken.None);
        print.IsReprint.Should().BeTrue();
        print.Reason.Should().Be("customer lost the copy");

        // A second copy can walk out of the shop, so head office is told.
        (await context.Outbox.CountAsync(
                e => e.Type == SyncEventType.SaleReceiptReprinted, CancellationToken.None))
            .Should().Be(1);
    }

    [Fact]
    public async Task ACustomerReturnsGoods_AndTheCashRefundLeavesTheDrawerShort()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        Result<SaleId> sold = await host.SellAsync(quantity: 3m, unitPaid: 75m);
        sold.IsSuccess.Should().BeTrue(sold.IsFailure ? sold.Error.ToString() : string.Empty);

        Result<SalesReturnId> returned = await host.ReturnAsync(sold.Value, quantity: 1m);
        returned.IsSuccess.Should().BeTrue(returned.IsFailure ? returned.Error.ToString() : string.Empty);

        Result<RefundId> refunded = await host.RefundAsync(sold.Value, returned.Value, amount: 25m);
        refunded.IsSuccess.Should().BeTrue(refunded.IsFailure ? refunded.Error.ToString() : string.Empty);

        // Returned goods do not go back on the shelf. They land in ReturnPending
        // until someone inspects them (OFFLINE_SYNC.md §1), so the sellable
        // count stays at seventeen and the returned unit is held separately.
        (await host.QuantityAsync(host.StoreId, InventoryState.Available)).Should().Be(17m);
        (await host.QuantityAsync(host.StoreId, InventoryState.ReturnPending)).Should().Be(1m);

        // The drawer holds the float plus 75 taken minus 25 paid back. Counting
        // 2,050 must balance — this is the case that would have reported a
        // twenty-five peso shortfall while refunds were reported as zero.
        Result<CashierShiftId> closed = await host.CloseShiftAsync(declared: 2050m, counted: 2050m);

        closed.IsSuccess.Should().BeTrue(closed.IsFailure ? closed.Error.ToString() : string.Empty);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        CashierShift shift = await context.LocalShifts.SingleAsync(CancellationToken.None);

        shift.CashVariance.Should().Be(0m, "the refund is counted out of the drawer, not ignored");

        (await context.Outbox.CountAsync(e => e.Type == SyncEventType.SalesReturnCreated, CancellationToken.None))
            .Should().Be(1);
        (await context.Outbox.CountAsync(e => e.Type == SyncEventType.RefundIssued, CancellationToken.None))
            .Should().Be(1);
    }

    [Fact]
    public async Task ASaleCannotBeRefundedPastWhatItWasPaid()
    {
        await using SaleHost host = await SaleHost.StartAsync();
        await host.ReceiveStockAsync(20m);

        Result<SaleId> sold = await host.SellAsync(quantity: 3m, unitPaid: 75m);
        Result<SalesReturnId> returned = await host.ReturnAsync(sold.Value, quantity: 3m);
        returned.IsSuccess.Should().BeTrue(returned.IsFailure ? returned.Error.ToString() : string.Empty);

        (await host.RefundAsync(sold.Value, returned.Value, amount: 75m)).IsSuccess.Should().BeTrue();

        // A second refund of the same cash would hand out money the sale never
        // took. The device answers from its own rows; the server re-checks.
        Result<RefundId> again = await host.RefundAsync(sold.Value, returned.Value, amount: 75m);

        again.IsFailure.Should().BeTrue();
    }

    /// <summary>A device container as MauiProgram composes one, with stock on the shelf.</summary>
    private sealed class SaleHost : IAsyncDisposable
    {
        private ServiceProvider provider = null!;

        public TemporaryDeviceDatabase Database { get; private set; } = null!;

        public LocationId StoreId { get; private set; }

        public LocationId CustomerLocationId { get; private set; }

        public ProductId ProductId { get; private set; }

        public UnitOfMeasureId UnitId { get; } = new(Guid.CreateVersion7());

        public DeviceId DeviceId { get; private set; }

        public UserId CashierId { get; private set; }

        public CashierShiftId ShiftId { get; private set; }

        public static async Task<SaleHost> StartAsync(bool grantSalePermission = true)
        {
            SaleHost host = new()
            {
                Database = await TemporaryDeviceDatabase.CreateAsync(),
                StoreId = LocationId.New(),
                CustomerLocationId = LocationId.New(),
                ProductId = ProductId.New(),
                CashierId = UserId.New(),
            };

            host.DeviceId = await host.Database.EnrolAsync("D03", host.StoreId);

            List<PermissionSnapshotGrant> grants =
            [
                new(Permissions.Sales.OpenShift, host.StoreId),
                new(Permissions.Sales.CloseShift, host.StoreId),
                new(Permissions.Sales.Void, host.StoreId),
                new(Permissions.Sales.Reprint, host.StoreId),
                new(Permissions.Sales.Return, host.StoreId),
                new(Permissions.Sales.Refund, host.StoreId),
                .. grantSalePermission
                    ? new[] { new PermissionSnapshotGrant(Permissions.Sales.Create, host.StoreId) }
                    : [],
            ];

            Result<ChangeFeedApplyOutcome> applied = await host.Database.Applier.ApplyAsync(
                new ChangeFeedPage(0, 9,
                [
                    new LocationChanged(2, host.StoreId, "STORE-1", "Store 1", LocationKind.Store,
                        "Asia/Manila", "PHP", true, LocationSettings.Default.ToJson()),
                    new LocationChanged(3, host.CustomerLocationId, SystemLocationCodes.ExternalCustomer,
                        "Customers", LocationKind.External, "Asia/Manila", "PHP", true),
                    new ProductChanged(4, host.ProductId, "SKU-1", "Rice 5kg", true, false, false, 1, Now),
                    new ProductPriceChanged(5, ProductPriceId.New(), host.ProductId, null, 25m, "PHP",
                        Now.AddDays(-30), null),
                    new PermissionSnapshotIssued(9, host.CashierId, 4, Now, Now.AddHours(12), grants),
                ]));
            applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.ToString() : string.Empty);

            DeviceSession session = new();
            session.SignIn(host.CashierId, host.DeviceId, host.StoreId);

            ServiceCollection services = [];
            services.AddLogging();
            services.AddOfflineClientApplication();
            services.AddSingleton(host.Database.Initializer);
            services.AddSingleton<ISystemClock>(host.Database.Clock);
            services.AddSingleton(session);
            services.AddSingleton<IDeviceProfileAccessor>(host.Database.Profiles);
            services.AddSingleton<ICurrentUser, DeviceCurrentUser>();
            services.AddSingleton<IPermissionEvaluator, DeviceSnapshotPermissionEvaluator>();
            services.AddSingleton<INegativeStockAttemptRecorder, DeviceNegativeStockAttemptRecorder>();
            services.AddScoped(sp => sp.GetRequiredService<DeviceDatabaseInitializer>().CreateDbContext());
            services.AddScoped<ILedgerStore>(sp => sp.GetRequiredService<PosDeviceDbContext>());
            services.AddScoped<ILedgerPolicyProvider, DeviceLedgerPolicyProvider>();
            services.AddScoped<IInventoryLedger, InventoryLedger>();
            services.AddScoped<IUnitOfWork, DeviceUnitOfWork>();
            services.AddScoped<IAuditWriter, DeviceAuditWriter>();
            services.AddScoped<IDeviceOutbox, DeviceOutbox>();
            services.AddScoped<IShiftRepository, DeviceShiftRepository>();
            services.AddScoped<ISalesRepository, DeviceSalesRepository>();
            services.AddScoped<ICustomerRepository, DeviceCustomerRepository>();
            services.AddScoped<IExpiryService, DeviceExpiryService>();
            services.AddScoped<IDocumentNumberGenerator, DeviceDocumentNumberGenerator>();

            host.provider = services.BuildServiceProvider();
            host.ShiftId = await host.OpenShiftAsync();
            return host;
        }

        public async Task<DocumentNumber> AllocateSaleNumberAsync()
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IDocumentNumberGenerator>()
                .NextAsync(DocumentType.Sale, CancellationToken.None);
        }

        /// <summary>Puts stock on the shelf the way a goods receipt would.</summary>
        public async Task ReceiveStockAsync(decimal quantity)
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            IInventoryLedger ledger = scope.ServiceProvider.GetRequiredService<IInventoryLedger>();
            LocationId supplier = LocationId.New();

            Result<PostedMovementGroup> posted = await ledger.PostAsync(
                new MovementGroupSpec(
                    EventId.New(),
                    InventoryMovementType.SupplierReceipt,
                    ReferenceDocumentType.GoodsReceipt,
                    Guid.CreateVersion7(),
                    "GRN-2026-000001",
                    [
                        new MovementLegSpec(ProductId, null, supplier, LocationKind.External,
                            InventoryState.External, -quantity, 18m, false),
                        new MovementLegSpec(ProductId, null, StoreId, LocationKind.Store,
                            InventoryState.Available, +quantity, 18m, false),
                    ],
                    new LedgerActor(CashierId, CashierId, DeviceId, CorrelationId.New()),
                    Now,
                    DateOnly.FromDateTime(Now.UtcDateTime)),
                CancellationToken.None);

            posted.IsSuccess.Should().BeTrue(posted.IsFailure ? posted.Error.ToString() : string.Empty);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        public async Task<Result<SaleId>> SellAsync(
            decimal quantity,
            decimal unitPaid,
            CustomerId? customerId = null,
            EventId? eventId = null,
            DocumentNumber? number = null)
        {
            DocumentNumber saleNumber = number ?? await AllocateSaleNumberAsync();

            return await SendAsync(new CompleteSaleCommand(
                saleNumber,
                eventId ?? EventId.New(),
                StoreId,
                ShiftId,
                DeviceId,
                CashierId,
                customerId,
                DateOnly.FromDateTime(Now.UtcDateTime),
                Now,
                [new CompleteSaleLine(ProductId, quantity, UnitId, null, null, null, 0m, null, false, null)],
                [new CompleteSalePayment(PaymentMethod.Cash, unitPaid, unitPaid, null)]));
        }

        public async Task<decimal> QuantityAsync(LocationId location, InventoryState state)
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IInventoryLedger>()
                .GetQuantityAsync(location, ProductId, BatchId.Empty, state, CancellationToken.None);
        }

        public Task<Result<TResult>> SendCommandAsync<TResult>(ICommand<TResult> command) => SendAsync(command);

        public async Task<Result<SalesReturnId>> ReturnAsync(SaleId saleId, decimal quantity)
        {
            DocumentNumber number;
            await using (AsyncServiceScope scope = this.provider.CreateAsyncScope())
            {
                number = await scope.ServiceProvider
                    .GetRequiredService<IDocumentNumberGenerator>()
                    .NextAsync(DocumentType.SalesReturn, CancellationToken.None);
            }

            return await SendAsync(new CreateSalesReturnCommand(
                number,
                EventId.New(),
                saleId,
                StoreId,
                ShiftId,
                DeviceId,
                null,
                DateOnly.FromDateTime(Now.UtcDateTime),
                Now,
                CashierId,
                [new SalesReturnLine(ProductId, quantity)]));
        }

        public Task<Result<RefundId>> RefundAsync(SaleId saleId, SalesReturnId returnId, decimal amount)
            => SendAsync(new RefundSalesReturnCommand(
                EventId.New(),
                saleId,
                returnId,
                StoreId,
                ShiftId,
                DeviceId,
                PaymentMethod.Cash,
                amount,
                amount,
                null,
                Now,
                CashierId));

        public Task<Result<CashierShiftId>> CloseShiftAsync(decimal declared, decimal counted)
            => SendAsync(new CloseShiftCommand(ShiftId, StoreId, declared, counted));

        private async Task<CashierShiftId> OpenShiftAsync()
        {
            DocumentNumber number;
            await using (AsyncServiceScope scope = this.provider.CreateAsyncScope())
            {
                number = await scope.ServiceProvider
                    .GetRequiredService<IDocumentNumberGenerator>()
                    .NextAsync(DocumentType.CashierShift, CancellationToken.None);
            }

            Result<CashierShiftId> opened = await SendAsync(
                new OpenShiftCommand(number, StoreId, DateOnly.FromDateTime(Now.UtcDateTime), 2000m));

            opened.IsSuccess.Should().BeTrue(opened.IsFailure ? opened.Error.ToString() : string.Empty);
            return opened.Value;
        }

        private async Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command)
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IDispatcher>()
                .SendAsync(command, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await this.provider.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
