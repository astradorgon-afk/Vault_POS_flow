using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Domain.Sales;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sync;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Tests.Sync;

/// <summary>
/// What head office does with a drawer that was opened, locked, unlocked and
/// counted while nobody could reach it. Each event is replayed through the same
/// aggregate the online handler uses, so a transition that would have been
/// refused at the till is refused here too rather than written as a status.
/// </summary>
/// <remarks>
/// The events go through the real <see cref="SyncPushProcessor"/> rather than
/// straight into an applier, because the two are only correct together: the
/// processor decides what a second event in the same batch may assume, and the
/// device sends the lifecycle as a batch.
/// </remarks>
public sealed class ShiftLifecycleAppliersTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 2, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Device = DeviceId.New();
    private static readonly LocationId Store = LocationId.New();
    private static readonly UserId Cashier = UserId.New();

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private SyncPushProcessor processor = null!;
    private RecordingAudit audit = null!;
    private long sequence;

    public async Task InitializeAsync()
    {
        this.connection = new SqliteConnection("Data Source=:memory:");
        await this.connection.OpenAsync();

        // No-tracking, as the server container configures it. Reads that feed a
        // write opt in explicitly there, and a fixture that tracked by default
        // would pass over the exact mistake that breaks in production.
        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(this.connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        this.context = new PosDbContext(options);
        await this.context.Database.EnsureCreatedAsync();

        ShiftRepository shifts = new(this.context);
        this.audit = new RecordingAudit();
        FixedClock clock = new(Now);

        this.processor = new SyncPushProcessor(this.context, clock,
        [
            new ShiftOpenedApplier(this.context, clock),
            new ShiftSuspendedApplier(shifts, this.audit),
            new ShiftResumedApplier(shifts, this.audit),
            new ShiftClosedApplier(shifts, this.audit),
        ],
            new CollectingNegativeStockRecorder());
    }

    public async Task DisposeAsync()
    {
        await this.context.DisposeAsync();
        await this.connection.DisposeAsync();
    }

    [Fact]
    public async Task ATillLockedAndUnlockedInOneBatch_LandsBothWays()
    {
        CashierShiftId shiftId = await OpenAsync();

        // One upload carrying both halves. The second event reads a row the
        // first one wrote, which is the case that breaks if the processor lets
        // an event inherit the previous event's change tracker.
        IReadOnlyList<SyncEventResult> results = await PushAsync(
            Event("ShiftSuspended", Payload(shiftId, ShiftStatus.Suspended)),
            Event("ShiftResumed", Payload(shiftId, ShiftStatus.Open)));

        results.Should().OnlyContain(r => r.Outcome == SyncOutcome.Accepted);
        (await LoadAsync(shiftId)).Status.Should().Be(ShiftStatus.Open);

        this.audit.Actions.Should().Equal(AuditActions.Sales.ShiftSuspended, AuditActions.Sales.ShiftResumed);
    }

    [Fact]
    public async Task AResumeOfAShiftThatWasNeverSuspended_IsRefusedByTheSameRuleAsOnline()
    {
        CashierShiftId shiftId = await OpenAsync();

        SyncEventResult result = await PushOneAsync("ShiftResumed", Payload(shiftId, ShiftStatus.Open));

        result.Outcome.Should().Be(SyncOutcome.Rejected);
        result.ErrorCode.Should().Be(ShiftErrors.ShiftInvalidState(ShiftStatus.Suspended, ShiftStatus.Open).Code);
        this.audit.Actions.Should().BeEmpty("nothing happened, so nothing is recorded as having happened");
    }

    [Fact]
    public async Task AnEventForAShiftTheServerNeverSaw_IsRefusedRatherThanInvented()
    {
        SyncEventResult result = await PushOneAsync(
            "ShiftSuspended", Payload(CashierShiftId.New(), ShiftStatus.Suspended));

        result.Outcome.Should().Be(SyncOutcome.Rejected);
        result.ErrorCode.Should().Be("sync.shift_unknown");
    }

    [Fact]
    public async Task ADeviceCannotSuspendAnotherRegistersShift()
    {
        CashierShiftId shiftId = await OpenAsync();
        DeviceId other = DeviceId.New();

        // The payload names the uploading device honestly; what it does not own
        // is the shift. Both halves of the check matter, so this exercises the
        // one the payload cannot lie its way past.
        SyncApplyResult result = await new ShiftSuspendedApplier(
            new ShiftRepository(this.context), this.audit).ApplyAsync(
                other,
                EventId.New(),
                CanonicalJson.Serialize(Payload(shiftId, ShiftStatus.Suspended) with { DeviceId = other.Value }),
                CancellationToken.None);

        result.Outcome.Should().Be(SyncOutcome.Rejected);
        result.ErrorCode.Should().Be("sync.device_mismatch");
    }

    [Fact]
    public async Task ClosingADrawer_DerivesTheVarianceFromWhatTheServerHolds_AndRecordsTheDisagreement()
    {
        CashierShiftId shiftId = await OpenAsync();

        // The device took a hundred pesos in cash it has not uploaded a sale for
        // yet, so it reported a balanced drawer. The server holds no such sale,
        // and its own figure is what the cash report will total.
        ShiftSyncPayload close = Payload(shiftId, ShiftStatus.Closed) with
        {
            ClosedAtUtc = Now.AddHours(8),
            DeclaredCash = 2100m,
            CountedCash = 2100m,
            CashVariance = 0m,
        };

        SyncEventResult result = await PushOneAsync("ShiftClosed", close);

        result.Outcome.Should().Be(SyncOutcome.Accepted, "the money has already moved");
        result.ServerDocumentNumber.Should().Be("SHF-2026-D03-0001");

        CashierShift shift = await LoadAsync(shiftId);
        shift.Status.Should().Be(ShiftStatus.Closed);
        shift.CountedCash.Should().Be(2100m, "the drawer was counted, and that is a fact about the drawer");
        shift.CashVariance.Should().Be(100m, "derived from the sales the server actually accepted");
        shift.ClosedAtUtc.Should().Be(Now.AddHours(8));

        this.audit.Actions.Should().Equal(AuditActions.Sales.ShiftClosed);
        this.audit.Entries[0].Reason.Should().NotBeNull(
            "a manager must be able to explain the Z-report, not merely contradict it");
        this.audit.Entries[0].NewValueJson.Should().Contain("deviceReportedVariance");
    }

    [Fact]
    public async Task ADrawerThatAgreesWithTheServer_ClosesWithoutARaisedReason()
    {
        CashierShiftId shiftId = await OpenAsync();

        ShiftSyncPayload close = Payload(shiftId, ShiftStatus.Closed) with
        {
            ClosedAtUtc = Now.AddHours(8),
            DeclaredCash = 2000m,
            CountedCash = 2000m,
            CashVariance = 0m,
        };

        (await PushOneAsync("ShiftClosed", close)).Outcome.Should().Be(SyncOutcome.Accepted);

        (await LoadAsync(shiftId)).CashVariance.Should().Be(0m);
        this.audit.Entries[0].Reason.Should().BeNull();
    }

    [Fact]
    public async Task ADeviceMayNotForceCloseItsOwnShift()
    {
        CashierShiftId shiftId = await OpenAsync();

        ShiftSyncPayload close = Payload(shiftId, ShiftStatus.Closed) with
        {
            ClosedAtUtc = Now.AddHours(8),
            DeclaredCash = 2000m,
            CountedCash = 2000m,
            IsForceClosed = true,
        };

        SyncEventResult result = await PushOneAsync("ShiftClosed", close);

        result.Outcome.Should().Be(SyncOutcome.Rejected);
        result.ErrorCode.Should().Be("sync.force_close_not_permitted");
        (await LoadAsync(shiftId)).Status.Should().Be(ShiftStatus.Open, "a flag must not be able to suppress a count");
    }

    [Fact]
    public async Task ACloseWithoutTheDrawerFigures_IsRefused()
    {
        CashierShiftId shiftId = await OpenAsync();

        SyncEventResult result = await PushOneAsync("ShiftClosed", Payload(shiftId, ShiftStatus.Closed));

        result.Outcome.Should().Be(SyncOutcome.Rejected);
        result.ErrorCode.Should().Be("sync.payload_invalid");
    }

    private async Task<CashierShiftId> OpenAsync()
    {
        CashierShiftId shiftId = CashierShiftId.New();

        SyncEventResult opened = await PushOneAsync("ShiftOpened", Payload(shiftId, ShiftStatus.Open));

        opened.Outcome.Should().Be(SyncOutcome.Accepted, opened.Message ?? string.Empty);
        return shiftId;
    }

    private async Task<SyncEventResult> PushOneAsync(string type, ShiftSyncPayload payload)
        => (await PushAsync(Event(type, payload)))[0];

    private async Task<IReadOnlyList<SyncEventResult>> PushAsync(params SyncPushEvent[] events)
    {
        SyncPushResponse response = await this.processor.ProcessAsync(
            new SyncPushRequest(Device.Value, Guid.CreateVersion7(), Now, 1_000L, events),
            CancellationToken.None);

        return response.Results;
    }

    private SyncPushEvent Event(string type, ShiftSyncPayload payload)
    {
        string json = CanonicalJson.Serialize(payload);

        return new SyncPushEvent(
            Guid.CreateVersion7(),
            ++this.sequence,
            type,
            Now,
            json,
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    private async Task<CashierShift> LoadAsync(CashierShiftId shiftId)
    {
        this.context.ChangeTracker.Clear();
        return await this.context.CashierShifts
            .AsNoTracking()
            .SingleAsync(s => s.Id == shiftId, CancellationToken.None);
    }

    private static ShiftSyncPayload Payload(CashierShiftId shiftId, ShiftStatus status)
        => new(
            shiftId.Value,
            "SHF-2026-D03-0001",
            Store.Value,
            Device.Value,
            Cashier.Value,
            2000m,
            new DateOnly(2026, 9, 17),
            Now,
            status.ToString());

    /// <summary>Keeps what the applier recorded, without needing a request context.</summary>
    private sealed class RecordingAudit : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public IEnumerable<string> Actions => this.Entries.Select(e => e.Action);

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            this.Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
