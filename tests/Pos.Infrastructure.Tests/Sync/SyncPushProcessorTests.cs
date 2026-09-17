using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Auditing;
using Pos.Domain.Common;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sync;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Tests.Sync;

/// <summary>
/// The upload engine. Everything here protects one promise: an event a device
/// sends twice takes effect once, and an event whose content changed between
/// sends takes effect not at all.
/// </summary>
public sealed class SyncPushProcessorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 2, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId Device = DeviceId.New();

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private SyncPushProcessor processor = null!;
    private CountingApplier applier = null!;

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

        this.applier = new CountingApplier { Context = this.context };
        this.processor = new SyncPushProcessor(this.context, new FixedClock(Now), [this.applier]);
    }

    public async Task DisposeAsync()
    {
        await this.context.DisposeAsync();
        await this.connection.DisposeAsync();
    }

    [Fact]
    public async Task AnEventIsAppliedOnceAndAnsweredFor()
    {
        SyncPushResponse response = await Push(Event(1, "{\"a\":1}"));

        response.Results.Should().ContainSingle();
        response.Results[0].Outcome.Should().Be(SyncOutcome.Accepted);
        this.applier.Applications.Should().Be(1);
    }

    [Fact]
    public async Task ARetryOfTheSameEventReplaysTheOriginalResult_WithoutApplyingAgain()
    {
        SyncPushEvent sent = Event(1, "{\"a\":1}");

        SyncPushResponse first = await Push(sent);
        SyncPushResponse retry = await Push(sent);

        first.Results[0].Outcome.Should().Be(SyncOutcome.Accepted);
        retry.Results[0].Outcome.Should().Be(SyncOutcome.Accepted, "the original verdict is replayed verbatim");
        retry.Results[0].AppliedAtUtc.Should().Be(first.Results[0].AppliedAtUtc);

        this.applier.Applications.Should().Be(1, "a network timeout must not sell the same goods twice");
    }

    [Fact]
    public async Task TheSameIdentifierCarryingDifferentContentIsRefusedAsTampering()
    {
        await Push(Event(1, "{\"a\":1}"));

        SyncPushEvent altered = Event(1, "{\"a\":2}", Event(1, "{\"a\":1}").EventId);
        SyncPushResponse response = await Push(altered);

        response.Results[0].Outcome.Should().Be(SyncOutcome.Rejected);
        response.Results[0].ErrorCode.Should().Be("sync.idempotency_key_reuse");

        this.applier.Applications.Should().Be(1, "nothing is applied for a payload that changed under a reused id");
    }

    [Fact]
    public async Task AGapDefersTheEventAndEverythingBehindIt()
    {
        SyncPushResponse response = await Push(Event(2, "{\"a\":2}"), Event(3, "{\"a\":3}"));

        response.Results.Should().HaveCount(2);
        response.Results.Should().OnlyContain(r => r.Outcome == SyncOutcome.Deferred);
        this.applier.Applications.Should().Be(0, "applying past a gap would act on a state the device never had");
    }

    [Fact]
    public async Task OnceTheMissingEventArrivesTheDeferredOnesAreAccepted()
    {
        await Push(Event(2, "{\"a\":2}"));

        SyncPushResponse response = await Push(Event(1, "{\"a\":1}"), Event(2, "{\"a\":2}"));

        response.Results.Should().OnlyContain(r => r.Outcome == SyncOutcome.Accepted);
        this.applier.Applications.Should().Be(2);
    }

    [Fact]
    public async Task ARefusedEventStillLetsTheNextOneThrough()
    {
        this.applier.RejectSequence = 1;

        SyncPushResponse response = await Push(Event(1, "{\"a\":1}"), Event(2, "{\"a\":2}"));

        response.Results[0].Outcome.Should().Be(SyncOutcome.Rejected);
        response.Results[1].Outcome.Should().Be(
            SyncOutcome.Accepted,
            "a refusal has been answered for; stalling everything behind it would strand the device forever");
    }

    [Fact]
    public async Task AnEventTypeThisServerDoesNotUnderstandIsRefusedRatherThanDropped()
    {
        SyncPushResponse response = await Push(Event(1, "{\"a\":1}") with { Type = "SomethingNewer" });

        response.Results[0].Outcome.Should().Be(SyncOutcome.Rejected);
        response.Results[0].ErrorCode.Should().Be("sync.event_type_unsupported");
    }

    [Fact]
    public async Task ASecondBatchContinuesFromWhereTheFirstStopped()
    {
        await Push(Event(1, "{\"a\":1}"), Event(2, "{\"a\":2}"));

        SyncPushResponse next = await Push(Event(3, "{\"a\":3}"));

        next.Results[0].Outcome.Should().Be(
            SyncOutcome.Accepted,
            "the checkpoint advances with every event, not only the first: a device that had to defer forever "
            + "after its first upload would never sync again");
    }

    [Fact]
    public async Task WhatARefusedEventTouchedBeforeItRefused_IsThrownAway()
    {
        this.applier.RejectSequence = 1;
        this.applier.StageBeforeAnswering = true;

        SyncPushResponse response = await Push(Event(1, "{\"a\":1}"), Event(2, "{\"a\":2}"));

        response.Results[0].Outcome.Should().Be(SyncOutcome.Rejected);
        response.Results[1].Outcome.Should().Be(SyncOutcome.Accepted);

        // The refused event staged an audit entry before it gave its verdict, as
        // a real applier does when it writes a note and then hits a rule several
        // steps later. Committing that alongside the refusal would leave the
        // audit trail describing something that never happened.
        List<string> recorded = await this.context.AuditLog
            .AsNoTracking()
            .Select(e => e.Action)
            .ToListAsync(CancellationToken.None);

        recorded.Should().ContainSingle(
            "only the event that took effect left a trace")
            .Which.Should().Be("test.applied");

        // And the refusal is still answered for, so the queue behind it moves.
        (await this.context.ProcessedEvents.AsNoTracking().CountAsync(CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task ClockSkewIsReportedRatherThanCorrected()
    {
        SyncPushResponse response = await Push(
            Event(1, "{\"a\":1}"),
            sentAt: Now.AddSeconds(90));

        response.ClockSkewSeconds.Should().BeApproximately(90d, 0.001d);
        response.ServerReceivedAtUtc.Should().Be(Now, "the server's clock is the one that counts");
    }

    private Task<SyncPushResponse> Push(params SyncPushEvent[] events)
        => Push(events, Now);

    private Task<SyncPushResponse> Push(SyncPushEvent single, DateTimeOffset sentAt)
        => Push([single], sentAt);

    private Task<SyncPushResponse> Push(SyncPushEvent[] events, DateTimeOffset sentAt)
        => this.processor.ProcessAsync(
            new SyncPushRequest(Device.Value, Guid.CreateVersion7(), sentAt, 1_000L, events),
            CancellationToken.None);

    /// <summary>
    /// A deterministic event: the identifier is derived from the sequence, so a
    /// test can resend "the same event" without holding on to it.
    /// </summary>
    private static SyncPushEvent Event(long sequence, string payload, Guid? eventId = null)
    {
        Guid id = eventId ?? new Guid((int)sequence, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

        return new SyncPushEvent(
            id,
            sequence,
            "Counted",
            Now,
            payload,
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
    }

    /// <summary>Counts how often the business effect actually ran.</summary>
    private sealed class CountingApplier : ISyncEventApplier
    {
        public string EventType => "Counted";

        public int Applications { get; private set; }

        public long? RejectSequence { get; set; }

        /// <summary>Writes a row before answering, as a real applier does.</summary>
        public bool StageBeforeAnswering { get; set; }

        public PosDbContext? Context { get; set; }

        public Task<SyncApplyResult> ApplyAsync(
            DeviceId deviceId,
            string payloadJson,
            CancellationToken cancellationToken)
        {
            Applications++;

            bool refusing = this.RejectSequence is not null && Applications == this.RejectSequence;

            if (this.StageBeforeAnswering && this.Context is { } context)
            {
                context.AuditLog.Add(AuditLogEntry.Record(
                    refusing ? "test.refused" : "test.applied",
                    "test",
                    Guid.CreateVersion7(),
                    Now,
                    CorrelationId.New(),
                    null,
                    null,
                    deviceId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
            }

            return Task.FromResult(refusing
                ? SyncApplyResult.Rejected("test.refused", "Refused by the test.")
                : SyncApplyResult.Accepted("DOC-1"));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
