using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Application.Common.Abstractions;
using Pos.Application.Notifications;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;

namespace Pos.Infrastructure.Tests.Ledger;

/// <summary>
/// Live stock announcements: a location is announced once its movement is
/// committed, never before, and never for a posting that was rolled back.
/// </summary>
public sealed class InventoryChangeInterceptorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly LocationId Main = LocationId.New();
    private static readonly LocationId Supplier = LocationId.New();
    private static readonly ProductId Coke = ProductId.New();
    private static readonly UserId Actor = UserId.New();

    private readonly RecordingPublisher _published = new();
    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private InventoryLedger _ledger = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(
                new AppendOnlyInterceptor(),
                new InventoryChangeInterceptor(_published, NullLogger<InventoryChangeInterceptor>.Instance))
            .Options;

        _context = new PosDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _ledger = new InventoryLedger(_context, new FixedClock(Now), new StrictLedgerPolicyProvider());
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task StandalonePosting_AnnouncesTheStockLocation_ButNotTheSupplierSide()
    {
        Result<PostedMovementGroup> posted = await _ledger.PostAsync(Receipt(10m), CancellationToken.None);
        posted.IsSuccess.Should().BeTrue();

        _published.Batches.Should().ContainSingle().Which.Should().Equal(Main);
    }

    [Fact]
    public async Task PostingInsideATransaction_IsAnnouncedOnlyWhenItCommits()
    {
        await using (IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync())
        {
            (await _ledger.PostAsync(Receipt(10m), CancellationToken.None)).IsSuccess.Should().BeTrue();
            await _context.SaveChangesAsync(CancellationToken.None);

            _published.Batches.Should().BeEmpty(because: "a reader told now would still see the old figures");

            await transaction.CommitAsync();
        }

        _published.Batches.Should().ContainSingle().Which.Should().Equal(Main);
    }

    [Fact]
    public async Task RolledBackPosting_IsNeverAnnounced()
    {
        await using (IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync())
        {
            (await _ledger.PostAsync(Receipt(10m), CancellationToken.None)).IsSuccess.Should().BeTrue();
            await _context.SaveChangesAsync(CancellationToken.None);
            await transaction.RollbackAsync();
        }

        _context.ChangeTracker.Clear();
        await using (IDbContextTransaction next = await _context.Database.BeginTransactionAsync())
        {
            await next.CommitAsync();
        }

        _published.Batches.Should().BeEmpty();
    }

    private static MovementGroupSpec Receipt(decimal quantity) => new(
        EventId.New(),
        InventoryMovementType.SupplierReceipt,
        ReferenceDocumentType.GoodsReceipt,
        Guid.CreateVersion7(),
        "GRN-2026-000001",
        [
            new(Coke, null, Supplier, LocationKind.External, InventoryState.External, -quantity, 45m, false),
            new(Coke, null, Main, LocationKind.MainWarehouse, InventoryState.Available, +quantity, 45m, false),
        ],
        new LedgerActor(Actor, Actor, null, CorrelationId.New()),
        Now,
        DateOnly.FromDateTime(Now.UtcDateTime));

    private sealed class RecordingPublisher : IInventoryChangePublisher
    {
        public List<LocationId[]> Batches { get; } = [];

        public Task PublishAsync(IReadOnlyCollection<LocationId> locations, CancellationToken cancellationToken)
        {
            Batches.Add([.. locations]);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
