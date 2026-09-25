using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// The upload queue. Everything here protects one property: an event that
/// reaches the server twice must be recognised as the same event, and an event
/// the device never committed must never reach it at all.
/// </summary>
public sealed class DeviceOutboxTests
{
    [Fact]
    public async Task AQueuedEvent_CarriesItsIdentity_SequenceAndHash()
    {
        await using OutboxHost host = await OutboxHost.StartAsync();

        OutboxEvent queued = await host.EnqueueAsync(SyncEventType.ShiftOpened, new { Float = 2000m });
        await host.SaveAsync();

        queued.DeviceSequence.Should().Be(1, "a device's first event is number one");
        queued.Status.Should().Be(OutboxStatus.Pending);
        queued.AttemptCount.Should().Be(0);
        queued.DeviceId.Should().Be(host.DeviceId);
        queued.UserId.Should().Be(host.CashierId);
        queued.PayloadHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(queued.PayloadJson)));
        queued.DeviceUptimeTicks.Should().BePositive("the monotonic clock is the tamper evidence");
    }

    [Fact]
    public async Task TheSequenceIsGaplessAndStrictlyIncreasing()
    {
        await using OutboxHost host = await OutboxHost.StartAsync();

        for (int i = 0; i < 5; i++)
        {
            await host.EnqueueAsync(SyncEventType.ShiftOpened, new { Index = i });
        }

        await host.SaveAsync();

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        List<long> sequences = await context.Outbox
            .AsNoTracking()
            .OrderBy(e => e.DeviceSequence)
            .Select(e => e.DeviceSequence)
            .ToListAsync(CancellationToken.None);

        sequences.Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task ARolledBackEventReleasesItsNumber_SoNoGapIsLeft()
    {
        await using OutboxHost host = await OutboxHost.StartAsync();

        await using (var transaction = await host.Context.Database.BeginTransactionAsync(CancellationToken.None))
        {
            await host.EnqueueAsync(SyncEventType.ShiftOpened, new { Abandoned = true });
            await host.SaveAsync();
            await transaction.RollbackAsync(CancellationToken.None);
        }

        host.Context.ChangeTracker.Clear();

        OutboxEvent afterwards = await host.EnqueueAsync(SyncEventType.ShiftOpened, new { Abandoned = false });
        await host.SaveAsync();

        afterwards.DeviceSequence.Should().Be(1, "the abandoned event gave its number back");

        await using PosDeviceDbContext context = await host.Database.OpenContextAsync();
        (await context.Outbox.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task EveryEventGetsItsOwnIdentity()
    {
        await using OutboxHost host = await OutboxHost.StartAsync();

        OutboxEvent first = await host.EnqueueAsync(SyncEventType.ShiftOpened, new { N = 1 });
        OutboxEvent second = await host.EnqueueAsync(SyncEventType.ShiftSuspended, new { N = 2 });
        await host.SaveAsync();

        first.EventId.Should().NotBe(second.EventId);
        first.EventId.Value.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UnsentWorkIsCounted_ButAcceptedWorkIsNot()
    {
        await using OutboxHost host = await OutboxHost.StartAsync();

        await host.EnqueueAsync(SyncEventType.ShiftOpened, new { N = 1 });
        await host.EnqueueAsync(SyncEventType.ShiftSuspended, new { N = 2 });
        await host.SaveAsync();

        (await host.Outbox.CountUnsentAsync(CancellationToken.None)).Should().Be(2);
    }

    [Theory]
    [InlineData(OutboxStatus.Pending, true)]
    [InlineData(OutboxStatus.Sending, true)]
    [InlineData(OutboxStatus.Failed, true)]
    [InlineData(OutboxStatus.RequiresReview, false)]
    [InlineData(OutboxStatus.Synchronized, false)]
    [InlineData(OutboxStatus.Conflict, false)]
    public async Task WhatCountsAsUnsent_IsWhatCouldStillBeLost(OutboxStatus status, bool counted)
    {
        await using OutboxHost host = await OutboxHost.StartAsync();

        OutboxEvent queued = await host.EnqueueAsync(SyncEventType.ShiftOpened, new { N = 1 });
        await host.SaveAsync();

        // Failed still counts: retries are never abandoned, so that work has not
        // reached head office yet. RequiresReview and Conflict are head office's
        // own answers, so it holds that work; a person resolves it there.
        await host.SetStatusAsync(queued.EventId, status);

        (await host.Outbox.CountUnsentAsync(CancellationToken.None)).Should().Be(counted ? 1 : 0);
    }

    [Fact]
    public void CanonicalJson_SortsPropertiesSoDeclarationOrderCannotChangeAHash()
    {
        string declaredOneWay = CanonicalJson.Serialize(new { Zebra = 1, Apple = 2, Mango = 3 });
        string declaredAnother = CanonicalJson.Serialize(new { Apple = 2, Mango = 3, Zebra = 1 });

        declaredOneWay.Should().Be(declaredAnother);
        declaredOneWay.Should().Be("""{"Apple":2,"Mango":3,"Zebra":1}""");
    }

    [Fact]
    public void CanonicalJson_SortsAtEveryDepth_AndLeavesArrayOrderAlone()
    {
        string json = CanonicalJson.Serialize(new
        {
            Outer = "z",
            Inner = new { Second = 2, First = 1 },
            Items = new[] { 3, 1, 2 },
        });

        json.Should().Be("""{"Inner":{"First":1,"Second":2},"Items":[3,1,2],"Outer":"z"}""");
    }

    [Fact]
    public void CanonicalJson_IsStableForTheSameContent()
    {
        var payload = new { Amount = 12.3456m, When = new DateTimeOffset(2026, 9, 17, 2, 0, 0, TimeSpan.Zero) };

        CanonicalJson.Serialize(payload).Should().Be(CanonicalJson.Serialize(payload));
    }

    /// <summary>A device store with one scoped context and an outbox over it.</summary>
    private sealed class OutboxHost : IAsyncDisposable
    {
        public TemporaryDeviceDatabase Database { get; private set; } = null!;

        public PosDeviceDbContext Context { get; private set; } = null!;

        public DeviceOutbox Outbox { get; private set; } = null!;

        public DeviceId DeviceId { get; private set; }

        public UserId CashierId { get; private set; }

        public LocationId LocationId { get; private set; }

        public static async Task<OutboxHost> StartAsync()
        {
            OutboxHost host = new()
            {
                Database = await TemporaryDeviceDatabase.CreateAsync(),
                CashierId = UserId.New(),
                LocationId = LocationId.New(),
            };

            host.DeviceId = await host.Database.EnrolAsync("D03", host.LocationId);
            host.Context = await host.Database.OpenContextAsync();

            DeviceSession session = new();
            session.SignIn(host.CashierId, host.DeviceId, host.LocationId);

            host.Outbox = new DeviceOutbox(
                host.Context,
                new DeviceCurrentUser(session),
                host.Database.Profiles,
                host.Database.Clock);

            return host;
        }

        public Task<OutboxEvent> EnqueueAsync<TPayload>(SyncEventType type, TPayload payload)
            => Outbox.EnqueueAsync(type, payload, LocationId, CancellationToken.None);

        public Task<int> SaveAsync() => Context.SaveChangesAsync(CancellationToken.None);

        public async Task SetStatusAsync(EventId eventId, OutboxStatus status)
        {
            await using PosDeviceDbContext context = await Database.OpenContextAsync();
            await context.Outbox
                .Where(e => e.EventId == eventId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status), CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
