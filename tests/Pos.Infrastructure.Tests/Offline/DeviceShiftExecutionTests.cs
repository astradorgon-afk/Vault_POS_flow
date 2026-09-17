using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Common.Offline;
using Pos.Application.Identity;
using Pos.Application.Sales;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// The first use case a device can actually execute, end to end through the
/// real container: the shift opens under a number the device minted itself,
/// authorized by a snapshot it cached, written to its own encrypted store.
/// Everything the boundary chunks built is exercised here at once.
/// </summary>
public sealed class DeviceShiftExecutionTests
{
    private static readonly DateTimeOffset Now = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task ADeviceOpensAShiftOffline_NumberedByItself_AndWritesAnAuditEntry()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();

        DocumentNumber number = await host.AllocateShiftNumberAsync();
        number.Value.Should().Be("SHF-2026-D03-0001");

        Result<CashierShiftId> opened = await host.SendAsync(
            new OpenShiftCommand(number, host.LocationId, new DateOnly(2026, 9, 17), 2000m));

        opened.IsSuccess.Should().BeTrue(opened.IsFailure ? opened.Error.ToString() : string.Empty);

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        CashierShift shift = await context.LocalShifts.SingleAsync(CancellationToken.None);

        shift.Number.Should().Be("SHF-2026-D03-0001");
        shift.Status.Should().Be(ShiftStatus.Open);
        shift.OpeningFloat.Should().Be(2000m);
        shift.CashierUserId.Should().Be(host.CashierId);

        DeviceLocalAudit entry = await context.LocalAudit.SingleAsync(CancellationToken.None);
        entry.Action.Should().Be(AuditActions.Sales.ShiftOpened);
        entry.UserId.Should().Be(host.CashierId);
        entry.DeviceId.Should().Be(host.DeviceId);
    }

    [Fact]
    public async Task ASecondShiftOnTheSameDevice_IsRefused()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();

        await host.OpenShiftAsync();

        Result<CashierShiftId> second = await host.SendAsync(new OpenShiftCommand(
            await host.AllocateShiftNumberAsync(), host.LocationId, new DateOnly(2026, 9, 17), 500m));

        second.IsFailure.Should().BeTrue("one drawer, one open shift");

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.LocalShifts.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task ANumberFromAnotherDevice_IsRefused()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();

        DocumentNumber foreign = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D07", 1);

        Result<CashierShiftId> opened = await host.SendAsync(
            new OpenShiftCommand(foreign, host.LocationId, new DateOnly(2026, 9, 17), 2000m));

        opened.IsFailure.Should().BeTrue("the handler checks the number against the device that claims it");
    }

    [Fact]
    public async Task SuspendAndResume_RoundTripThroughTheDeviceStore()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();
        CashierShiftId shiftId = await host.OpenShiftAsync();

        (await host.SendAsync(new SuspendShiftCommand(shiftId, host.LocationId))).IsSuccess.Should().BeTrue();
        await host.AssertStatusAsync(shiftId, ShiftStatus.Suspended);

        (await host.SendAsync(new ResumeShiftCommand(shiftId, host.LocationId))).IsSuccess.Should().BeTrue();
        await host.AssertStatusAsync(shiftId, ShiftStatus.Open);
    }

    [Fact]
    public async Task WithoutThePermissionInTheSnapshot_TheShiftDoesNotOpen()
    {
        await using DeviceHost host = await DeviceHost.StartAsync(grantShiftPermission: false);

        Result<CashierShiftId> opened = await host.SendAsync(new OpenShiftCommand(
            await host.AllocateShiftNumberAsync(), host.LocationId, new DateOnly(2026, 9, 17), 2000m));

        opened.IsFailure.Should().BeTrue();
        opened.Error.Code.Should().Be("auth.permission_denied");

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.LocalShifts.AnyAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task OnceTheCachedSnapshotExpires_TheDeviceStopsOpeningShifts()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();

        host.Database.Clock.UtcNow = Now.AddHours(13);

        Result<CashierShiftId> opened = await host.SendAsync(new OpenShiftCommand(
            await host.AllocateShiftNumberAsync(), host.LocationId, new DateOnly(2026, 9, 17), 2000m));

        opened.IsFailure.Should().BeTrue();
        opened.Error.Code.Should().Be("auth.permission_denied", "expiry is re-checked at every evaluation");
    }

    [Fact]
    public async Task AUseCaseADeviceDoesNotCarry_StillFailsClosed()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();
        CashierShiftId shiftId = await host.OpenShiftAsync();

        // Closing reconciles against local sales the device does not have, so it
        // is not registered. It must refuse before reaching the repository member
        // that would throw.
        Result<CashierShiftId> closed = await host.SendAsync(
            new CloseShiftCommand(shiftId, host.LocationId, 2000m, 2000m));

        closed.IsFailure.Should().BeTrue();
        closed.Error.Code.Should().Be("application.handler_unavailable");
        closed.Error.Type.Should().Be(ErrorType.Unavailable);
    }

    [Fact]
    public async Task OpeningAShift_QueuesTheBusinessEventInTheSameTransaction()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();
        CashierShiftId shiftId = await host.OpenShiftAsync();

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        OutboxEvent queued = await context.Outbox.SingleAsync(CancellationToken.None);

        queued.Type.Should().Be(SyncEventType.ShiftOpened);
        queued.DeviceSequence.Should().Be(1);
        queued.Status.Should().Be(OutboxStatus.Pending);
        queued.DeviceId.Should().Be(host.DeviceId);
        queued.LocationId.Should().Be(host.LocationId);

        // The business event, not the row: what the server replays.
        queued.PayloadJson.Should().Contain(shiftId.Value.ToString());
        queued.PayloadJson.Should().Contain("SHF-2026-D03-0001");
        queued.PayloadJson.Should().NotContain("local_cashier_shift");
    }

    [Fact]
    public async Task SuspendAndResume_EachQueueTheirOwnEvent()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();
        CashierShiftId shiftId = await host.OpenShiftAsync();

        await host.SendAsync(new SuspendShiftCommand(shiftId, host.LocationId));
        await host.SendAsync(new ResumeShiftCommand(shiftId, host.LocationId));

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        List<SyncEventType> queued = await context.Outbox
            .AsNoTracking()
            .OrderBy(e => e.DeviceSequence)
            .Select(e => e.Type)
            .ToListAsync(CancellationToken.None);

        queued.Should().Equal(
            SyncEventType.ShiftOpened, SyncEventType.ShiftSuspended, SyncEventType.ShiftResumed);
    }

    [Fact]
    public async Task ARefusedCommand_QueuesNothing()
    {
        await using DeviceHost host = await DeviceHost.StartAsync(grantShiftPermission: false);

        await host.SendAsync(new OpenShiftCommand(
            await host.AllocateShiftNumberAsync(), host.LocationId, new DateOnly(2026, 9, 17), 2000m));

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.Outbox.AnyAsync(CancellationToken.None))
            .Should().BeFalse("an event the device never committed must never reach head office");
    }

    [Fact]
    public async Task TheStatusBanner_ReportsWorkHeadOfficeHasNotSeen()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();

        DeviceStatusProvider status = new(
            host.Database.Initializer, new AssumeOfflineConnectivityProbe(), host.Database.Clock);

        (await status.GetAsync(host.CashierId, CancellationToken.None))
            .UnsentEvents.Should().Be(0);

        await host.OpenShiftAsync();

        (await status.GetAsync(host.CashierId, CancellationToken.None))
            .UnsentEvents.Should().Be(1, "the cashier can see nothing would be lost silently");
    }

    [Fact]
    public async Task AFailedCommand_LeavesNothingBehind()
    {
        await using DeviceHost host = await DeviceHost.StartAsync();

        DocumentNumber foreign = DocumentNumber.CreateForDevice(DocumentType.CashierShift, 2026, "D07", 1);
        await host.SendAsync(new OpenShiftCommand(foreign, host.LocationId, new DateOnly(2026, 9, 17), 2000m));

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.LocalShifts.AnyAsync(CancellationToken.None)).Should().BeFalse();
        (await context.LocalAudit.AnyAsync(CancellationToken.None))
            .Should().BeFalse("the unit of work rolled the audit entry back with the shift");
        (await context.Outbox.AnyAsync(CancellationToken.None))
            .Should().BeFalse("and the outbox event with them");
    }

    /// <summary>
    /// A device container as MauiProgram composes one: the whitelisted use cases
    /// plus the device adapters, over one encrypted store.
    /// </summary>
    private sealed class DeviceHost : IAsyncDisposable
    {
        private ServiceProvider provider = null!;

        public TemporaryDeviceDatabase Database { get; private set; } = null!;

        public DeviceId DeviceId { get; private set; }

        public LocationId LocationId { get; private set; }

        public UserId CashierId { get; private set; }

        public static async Task<DeviceHost> StartAsync(bool grantShiftPermission = true)
        {
            DeviceHost host = new()
            {
                Database = await TemporaryDeviceDatabase.CreateAsync(),
                LocationId = LocationId.New(),
                CashierId = UserId.New(),
            };

            host.DeviceId = await host.Database.EnrolAsync("D03", host.LocationId);

            Result<ChangeFeedApplyOutcome> applied = await host.Database.Applier.ApplyAsync(
                new ChangeFeedPage(0, 6,
                [
                    new LocationChanged(
                        3, host.LocationId, "STORE-1", "Store 1", LocationKind.Store, "Asia/Manila", "PHP", true,
                        LocationSettings.Default.ToJson()),
                    new PermissionSnapshotIssued(
                        6, host.CashierId, 9, Now, Now.AddHours(12),
                        grantShiftPermission
                            ? [new PermissionSnapshotGrant(Permissions.Sales.OpenShift, host.LocationId)]
                            : [new PermissionSnapshotGrant(Permissions.Catalog.View, host.LocationId)]),
                ]));
            applied.IsSuccess.Should().BeTrue(applied.IsFailure ? applied.Error.ToString() : string.Empty);

            DeviceSession session = new();
            session.SignIn(host.CashierId, host.DeviceId, host.LocationId);

            ServiceCollection services = [];
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddOfflineClientApplication();
            services.AddSingleton(host.Database.Initializer);
            services.AddSingleton<ISystemClock>(host.Database.Clock);
            services.AddSingleton(session);
            services.AddSingleton<IDeviceProfileAccessor>(host.Database.Profiles);
            services.AddSingleton<ICurrentUser, DeviceCurrentUser>();
            services.AddSingleton<IPermissionEvaluator, DeviceSnapshotPermissionEvaluator>();
            services.AddSingleton<INegativeStockAttemptRecorder, DeviceNegativeStockAttemptRecorder>();
            services.AddScoped(sp => sp.GetRequiredService<DeviceDatabaseInitializer>().CreateDbContext());
            services.AddScoped<IUnitOfWork, DeviceUnitOfWork>();
            services.AddScoped<IAuditWriter, DeviceAuditWriter>();
            services.AddScoped<IDeviceOutbox, DeviceOutbox>();
            services.AddScoped<IShiftRepository, DeviceShiftRepository>();
            services.AddScoped<IDocumentNumberGenerator, DeviceDocumentNumberGenerator>();

            host.provider = services.BuildServiceProvider();
            return host;
        }

        public async Task<DocumentNumber> AllocateShiftNumberAsync()
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IDocumentNumberGenerator>()
                .NextAsync(DocumentType.CashierShift, CancellationToken.None);
        }

        public async Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command)
        {
            await using AsyncServiceScope scope = this.provider.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<IDispatcher>()
                .SendAsync(command, CancellationToken.None);
        }

        public async Task<CashierShiftId> OpenShiftAsync()
        {
            Result<CashierShiftId> opened = await SendAsync(new OpenShiftCommand(
                await AllocateShiftNumberAsync(), LocationId, new DateOnly(2026, 9, 17), 2000m));

            opened.IsSuccess.Should().BeTrue(opened.IsFailure ? opened.Error.ToString() : string.Empty);
            return opened.Value;
        }

        public async Task AssertStatusAsync(CashierShiftId shiftId, ShiftStatus expected)
        {
            await using PosDeviceDbContext context = await Database.OpenContextAsync();
            CashierShift shift = await context.LocalShifts.SingleAsync(s => s.Id == shiftId, CancellationToken.None);
            shift.Status.Should().Be(expected);
        }

        public async ValueTask DisposeAsync()
        {
            await this.provider.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
