using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// Draining the outbox. Everything here protects one promise: an event that
/// could not be delivered is still there tomorrow, and an event nobody can
/// deliver is in front of a person rather than in a log.
/// </summary>
public sealed class SyncUploaderTests
{
    private static readonly DateTimeOffset Now = TemporaryDeviceDatabase.Now;

    [Fact]
    public async Task AnAcceptedEvent_IsMarkedSynchronisedAndNotSentAgain()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(3);

        host.Transport.Answer = events => Verdicts(events, SyncOutcome.Accepted);

        SyncUploadOutcome first = await host.RunAsync();

        first.Sent.Should().Be(3);
        first.Accepted.Should().Be(3);
        (await host.StatusesAsync()).Should().AllBeEquivalentTo(OutboxStatus.Synchronized);

        SyncUploadOutcome second = await host.RunAsync();
        second.Should().Be(SyncUploadOutcome.Idle, "nothing is sent twice once it has been answered for");
    }

    [Fact]
    public async Task ADuplicateCountsAsDelivered()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(1);

        host.Transport.Answer = events => Verdicts(events, SyncOutcome.Duplicate);

        (await host.RunAsync()).Accepted.Should().Be(1);
        (await host.StatusesAsync()).Should().AllBeEquivalentTo(
            OutboxStatus.Synchronized,
            "the server already has it; retrying forever would be the only alternative");
    }

    [Fact]
    public async Task AServerThatCannotBeReached_SchedulesARetryAndKeepsTheEvent()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(2);

        host.Transport.Answer = _ => null;

        SyncUploadOutcome outcome = await host.RunAsync();

        outcome.Unreachable.Should().BeTrue();
        outcome.Escalated.Should().Be(0);

        List<OutboxEvent> queued = await host.EventsAsync();
        queued.Should().AllSatisfy(e =>
        {
            e.Status.Should().Be(OutboxStatus.Pending);
            e.AttemptCount.Should().Be(1);
            e.NextRetryAtUtc.Should().NotBeNull();
            e.NextRetryAtUtc.Should().BeAfter(Now);
            e.LastError.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task AnEventWhoseRetryIsNotDueYet_IsLeftAlone()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(1);

        host.Transport.Answer = _ => null;
        await host.RunAsync();

        host.Transport.Calls = 0;
        SyncUploadOutcome outcome = await host.RunAsync();

        outcome.Should().Be(SyncUploadOutcome.Idle);
        host.Transport.Calls.Should().Be(0, "hammering a server that is already struggling is how an outage is extended");
    }

    [Fact]
    public async Task AfterEightConsecutiveFailures_TheEventIsEscalatedRatherThanRetriedForever()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(1);

        host.Transport.Answer = _ => null;

        for (int attempt = 1; attempt <= 8; attempt++)
        {
            await host.RunAsync();
            host.Clock.UtcNow = host.Clock.UtcNow.AddHours(1);
        }

        OutboxEvent queued = (await host.EventsAsync()).Single();

        queued.AttemptCount.Should().Be(8);
        queued.Status.Should().Be(OutboxStatus.Failed);
        queued.NextRetryAtUtc.Should().BeNull("it is not waiting for anything any more; it is waiting for somebody");
        queued.PayloadJson.Should().NotBeEmpty("kept forever: the thing that could not be sent is the thing to look at");
    }

    [Fact]
    public async Task ARefusedEvent_IsNeverRetried_AndKeepsTheServersOwnWords()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(1);

        host.Transport.Answer = events =>
        [
            .. events.Select(e => new SyncEventResult(
                e.EventId,
                SyncOutcome.Rejected,
                ErrorCode: "sale.payment_mismatch",
                Message: "The payments do not settle the sale.")),
        ];

        SyncUploadOutcome outcome = await host.RunAsync();

        outcome.Escalated.Should().Be(1);

        OutboxEvent queued = (await host.EventsAsync()).Single();
        queued.Status.Should().Be(OutboxStatus.Rejected);
        queued.LastError.Should().Be("sale.payment_mismatch");
        queued.ServerResponseJson.Should().Be("The payments do not settle the sale.");
        queued.NextRetryAtUtc.Should().BeNull();

        host.Clock.UtcNow = host.Clock.UtcNow.AddDays(1);
        host.Transport.Calls = 0;

        (await host.RunAsync()).Should().Be(SyncUploadOutcome.Idle);
        host.Transport.Calls.Should().Be(0, "the answer would not change, so asking again is only noise");
    }

    [Fact]
    public async Task AnEventTheServerFlagged_StopsBeingSent_ButStaysVisible()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(1);

        host.Transport.Answer = events =>
        [
            .. events.Select(e => new SyncEventResult(
                e.EventId,
                SyncOutcome.RequiresReview,
                ErrorCode: "sync.cashier_permission_withdrawn",
                Message: "The cashier no longer holds the authority to sell here.")),
        ];

        (await host.RunAsync()).Escalated.Should().Be(1);

        OutboxEvent queued = (await host.EventsAsync()).Single();
        queued.Status.Should().Be(OutboxStatus.RequiresReview);
        queued.LastError.Should().Be("sync.cashier_permission_withdrawn");
    }

    [Fact]
    public async Task ADeferredEvent_WaitsAndIsTriedAgain()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(1);

        host.Transport.Answer = events => Verdicts(events, SyncOutcome.Deferred);

        SyncUploadOutcome outcome = await host.RunAsync();

        outcome.Deferred.Should().Be(1);
        outcome.Escalated.Should().Be(0);

        OutboxEvent queued = (await host.EventsAsync()).Single();
        queued.Status.Should().Be(OutboxStatus.Pending, "deferred is the server saying 'not yet', not 'no'");
        queued.NextRetryAtUtc.Should().BeAfter(Now);
    }

    [Fact]
    public async Task AnEventTheServerDidNotAnswerFor_IsTreatedAsUnsent()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(2);

        // A response that covers only the first event. Assuming either way about
        // the second would either lose it or double it.
        host.Transport.Answer = events => [Verdicts(events, SyncOutcome.Accepted)[0]];

        await host.RunAsync();

        List<OutboxEvent> queued = await host.EventsAsync();
        queued[0].Status.Should().Be(OutboxStatus.Synchronized);
        queued[1].Status.Should().Be(OutboxStatus.Pending);
        queued[1].LastError.Should().Be("sync.no_result_for_event");
    }

    [Fact]
    public async Task EventsGoUpInSequenceOrder()
    {
        await using UploadHost host = await UploadHost.StartAsync();
        await host.QueueAsync(5);

        host.Transport.Answer = events => Verdicts(events, SyncOutcome.Accepted);
        await host.RunAsync();

        host.Transport.LastRequest!.Events.Select(e => e.DeviceSequence)
            .Should().BeInAscendingOrder("the server applies them in the order the till produced them");
    }

    [Fact]
    public void TheBackoffDoublesFromFiveSecondsAndStopsAtHalfAnHour()
    {
        SyncRetryPolicy policy = new(new Random(1));

        // Jitter is ±20%, so each delay is checked against its band rather than
        // an exact figure; the shape is what matters.
        Band(policy.DelayFor(1), 5);
        Band(policy.DelayFor(2), 10);
        Band(policy.DelayFor(3), 20);
        Band(policy.DelayFor(8), 640);
        Band(policy.DelayFor(20), 1800);
        Band(policy.DelayFor(64), 1800);

        static void Band(TimeSpan actual, double expectedSeconds)
            => actual.TotalSeconds.Should().BeInRange(expectedSeconds * 0.8, expectedSeconds * 1.2);
    }

    [Fact]
    public void TwoRegistersComingBackFromTheSameOutage_DoNotArriveTogether()
    {
        SyncRetryPolicy first = new(new Random(1));
        SyncRetryPolicy second = new(new Random(2));

        // Without jitter every register in the chain would retry on the same
        // tick, and the herd is the shape of the outage that caused it.
        first.DelayFor(6).Should().NotBe(second.DelayFor(6));
    }

    private static List<SyncEventResult> Verdicts(IReadOnlyList<SyncPushEvent> events, SyncOutcome outcome)
        => [.. events.Select(e => new SyncEventResult(e.EventId, outcome))];

    /// <summary>A device with an outbox and a transport that answers as the test says.</summary>
    private sealed class UploadHost : IAsyncDisposable
    {
        public TemporaryDeviceDatabase Database { get; private set; } = null!;

        public PosDeviceDbContext Context { get; private set; } = null!;

        public FakeTransport Transport { get; } = new();

        public TemporaryDeviceDatabase.MovableClock Clock => Database.Clock;

        public LocationId LocationId { get; private set; }

        private SyncUploader Uploader { get; set; } = null!;

        private DeviceOutbox Outbox { get; set; } = null!;

        public static async Task<UploadHost> StartAsync()
        {
            UploadHost host = new()
            {
                Database = await TemporaryDeviceDatabase.CreateAsync(),
                LocationId = LocationId.New(),
            };

            DeviceId deviceId = await host.Database.EnrolAsync("D03", host.LocationId);
            host.Context = await host.Database.OpenContextAsync();

            DeviceSession session = new();
            session.SignIn(UserId.New(), deviceId, host.LocationId);

            host.Outbox = new DeviceOutbox(
                host.Context,
                new DeviceCurrentUser(session),
                host.Database.Profiles,
                host.Database.Clock);

            host.Uploader = new SyncUploader(
                host.Context,
                host.Transport,
                host.Database.Profiles,
                host.Database.Clock,
                new SyncRetryPolicy(new Random(7)));

            return host;
        }

        public async Task QueueAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await Outbox.EnqueueAsync(
                    SyncEventType.ShiftOpened, new { Index = i }, LocationId, CancellationToken.None);
            }

            await Context.SaveChangesAsync(CancellationToken.None);
        }

        public Task<SyncUploadOutcome> RunAsync() => Uploader.RunOnceAsync(CancellationToken.None);

        public async Task<List<OutboxEvent>> EventsAsync()
        {
            Context.ChangeTracker.Clear();

            return await Context.Outbox
                .AsNoTracking()
                .OrderBy(e => e.DeviceSequence)
                .ToListAsync(CancellationToken.None);
        }

        public async Task<List<OutboxStatus>> StatusesAsync()
            => [.. (await EventsAsync()).Select(e => e.Status)];

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    /// <summary>Answers a batch however the test says, or not at all.</summary>
    private sealed class FakeTransport : ISyncTransport
    {
        public Func<IReadOnlyList<SyncPushEvent>, List<SyncEventResult>?> Answer { get; set; } = _ => null;

        public int Calls { get; set; }

        public SyncPushRequest? LastRequest { get; private set; }

        public Task<Result<SyncPullResponse>> PullAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("These tests only push.");

        public Task<Result<SyncBaselineResponse>> BaselineAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("These tests only push.");

        public Task<Result<SyncPushResponse>> PushAsync(
            SyncPushRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;

            List<SyncEventResult>? results = this.Answer(request.Events);

            return Task.FromResult(results is null
                ? Result<SyncPushResponse>.Failure(Error.Unavailable("sync.unreachable", "No route to the server."))
                : Result<SyncPushResponse>.Success(
                    new SyncPushResponse(request.ClientSentAtUtc, 0d, results)));
        }
    }
}
