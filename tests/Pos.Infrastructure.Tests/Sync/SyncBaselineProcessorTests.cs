using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Sync;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Tests.Sync;

/// <summary>
/// What a register is given to start from. The feed carries changes, so a
/// register that has applied nothing has nothing to read; this is what it reads
/// instead, and getting it wrong is how a till ends up unable to price or sell.
/// </summary>
public sealed class SyncBaselineProcessorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 2, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Payloads = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new StronglyTypedIdJsonConverter() },
    };

    private SqliteConnection connection = null!;
    private PosDbContext context = null!;
    private LocationId store;
    private DeviceId device;

    public async Task InitializeAsync()
    {
        this.connection = new SqliteConnection("Data Source=:memory:");
        await this.connection.OpenAsync();

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(this.connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(new ChangeFeedRecorder(new FixedClock(Now)))
            .Options;

        this.context = new PosDbContext(options);
        await this.context.Database.EnsureCreatedAsync();

        Location location = Location.Create(
            OrganizationId.New(), "S1", "Store One", LocationKind.Store, "Asia/Manila").Value;
        this.context.Locations.Add(location);
        await this.context.SaveChangesAsync();
        this.store = location.Id;

        Device register = Device.Register(
            "S101", "Register One", this.store, DevicePlatform.Android, Now, UserId.New()).Value;
        this.context.Set<Device>().Add(register);
        await this.context.SaveChangesAsync();
        this.device = register.Id;

        this.context.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await this.context.DisposeAsync();
        await this.connection.DisposeAsync();
    }

    [Fact]
    public async Task ADeviceThatIsNotRegistered_IsGivenNothing()
        => (await BuildAsync(DeviceId.New())).Should().BeNull(
            "a baseline is a device's whole catalogue, and an unknown caller is not owed one");

    [Fact]
    public async Task TheBaselineCarriesTheCatalogue_AndResumesWhereTheFeedStands()
    {
        await AddProductAsync("SKU-1", "Biscuits");

        long highest = await this.context.ChangeFeed.MaxAsync(e => e.Sequence);

        SyncBaselineResponse baseline = (await BuildAsync(this.device))!;

        baseline.ResumeCursor.Should().Be(
            highest,
            "the state was read as the feed stood, so the next pull starts from exactly there");

        baseline.Changes.Select(c => c.Kind).Should().Contain(
            [nameof(ProductChanged), nameof(LocationChanged)]);
    }

    [Fact]
    public async Task TheBaselineBurnsNoFeedSequence()
    {
        await AddProductAsync("SKU-2", "Soap");
        long before = await this.context.ChangeFeed.MaxAsync(e => e.Sequence);

        _ = await BuildAsync(this.device);
        await AddProductAsync("SKU-3", "Rice");

        long after = await this.context.ChangeFeed.MaxAsync(e => e.Sequence);

        // A baseline records nothing that happened, so the change made after it
        // takes the very next number. Reserving numbers instead would push a
        // device's cursor past rows the server still had to write.
        after.Should().Be(before + 1);
    }

    [Fact]
    public async Task TheBaselineNumbersItsOwnRowsFromOne()
    {
        await AddProductAsync("SKU-4", "Salt");

        SyncBaselineResponse baseline = (await BuildAsync(this.device))!;

        List<long> sequences =
        [
            .. baseline.Changes.Select(c => c.Change.GetProperty("sequence").GetInt64()),
        ];

        sequences.Should().Equal([.. Enumerable.Range(1, baseline.Changes.Count).Select(i => (long)i)]);
    }

    [Fact]
    public async Task ALivePermissionSnapshot_TravelsWithTheBaseline()
    {
        UserId cashier = UserId.New();
        await IssueSnapshotAsync(cashier, Now.AddHours(8));

        SyncBaselineResponse baseline = (await BuildAsync(this.device))!;

        // The server keeps no copy of a snapshot, so the feed is where it lives.
        // A baseline that walked the cursor past it would leave the cashier
        // signed in and unable to sell.
        baseline.Changes.Should().ContainSingle(c => c.Kind == nameof(PermissionSnapshotIssued));
    }

    [Fact]
    public async Task AnExpiredSnapshot_DoesNotTravel()
    {
        await IssueSnapshotAsync(UserId.New(), Now.AddYears(-1));

        SyncBaselineResponse baseline = (await BuildAsync(this.device))!;

        baseline.Changes.Should().NotContain(c => c.Kind == nameof(PermissionSnapshotIssued));
    }

    [Fact]
    public async Task ARevokedSnapshot_DoesNotTravel()
    {
        UserId cashier = UserId.New();
        await IssueSnapshotAsync(cashier, Now.AddHours(8));
        await RevokeSnapshotAsync(cashier);

        SyncBaselineResponse baseline = (await BuildAsync(this.device))!;

        baseline.Changes.Should().NotContain(c => c.Kind == nameof(PermissionSnapshotIssued));
    }

    [Fact]
    public async Task OnlyTheNewestSnapshotForAUser_Travels()
    {
        UserId cashier = UserId.New();
        await IssueSnapshotAsync(cashier, Now.AddHours(1));
        await IssueSnapshotAsync(cashier, Now.AddHours(9));

        SyncBaselineResponse baseline = (await BuildAsync(this.device))!;

        List<SyncPullChange> snapshots =
        [
            .. baseline.Changes.Where(c => c.Kind == nameof(PermissionSnapshotIssued)),
        ];

        snapshots.Should().ContainSingle();
        snapshots[0].Change.GetProperty("expiresAtUtc").GetDateTimeOffset()
            .Should().Be(Now.AddHours(9), "a later sign-in replaces what the earlier one granted");
    }

    private Task<SyncBaselineResponse?> BuildAsync(DeviceId id)
        => new SyncBaselineProcessor(this.context).BuildAsync(id, CancellationToken.None);

    private async Task AddProductAsync(string sku, string name)
    {
        this.context.Products.Add(
            Product.Create(sku, name, CategoryId.New(), UnitOfMeasureId.New(), UserId.New()).Value);
        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private async Task IssueSnapshotAsync(UserId userId, DateTimeOffset expiresAtUtc)
    {
        long sequence = await ChangeFeedSequenceAllocator.NextAsync(this.context, CancellationToken.None);

        this.context.ChangeFeed.Add(new ChangeFeedEntry(
            sequence,
            nameof(PermissionSnapshotIssued),
            this.store.Value,
            JsonSerializer.Serialize(
                new PermissionSnapshotIssued(
                    sequence,
                    userId,
                    1,
                    Now,
                    expiresAtUtc,
                    [new PermissionSnapshotGrant("sales.sell", this.store)]),
                Payloads),
            Now));

        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private async Task RevokeSnapshotAsync(UserId userId)
    {
        long sequence = await ChangeFeedSequenceAllocator.NextAsync(this.context, CancellationToken.None);

        this.context.ChangeFeed.Add(new ChangeFeedEntry(
            sequence,
            nameof(PermissionSnapshotRevoked),
            this.store.Value,
            JsonSerializer.Serialize(new PermissionSnapshotRevoked(sequence, userId), Payloads),
            Now));

        await this.context.SaveChangesAsync();
        this.context.ChangeTracker.Clear();
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
