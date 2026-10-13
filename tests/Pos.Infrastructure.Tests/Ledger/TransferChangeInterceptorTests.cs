using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Application.Notifications;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Persistence;
using Pos.Infrastructure.Persistence.Interceptors;

namespace Pos.Infrastructure.Tests.Ledger;

/// <summary>
/// Live transfer announcements: both ends of a document are announced once its
/// change is committed, never before, and never for an attempt that was rolled
/// back — the store waiting on a restock request hears about it at the moment
/// the owner's decision is durable.
/// </summary>
public sealed class TransferChangeInterceptorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly UserId Actor = UserId.New();

    private readonly RecordingPublisher _published = new();
    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private LocationId _main;
    private LocationId _store;
    private ProductId _coke;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(
                new AppendOnlyInterceptor(),
                new TransferChangeInterceptor(_published, NullLogger<TransferChangeInterceptor>.Instance))
            .Options;

        _context = new PosDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        Location main = Location.Create(Organization.DefaultId, "MAIN01", "Main warehouse", LocationKind.MainWarehouse, "Asia/Manila").Value;
        Location store = Location.Create(Organization.DefaultId, "STORE01", "Store one", LocationKind.Store, "Asia/Manila").Value;
        _context.Locations.AddRange(main, store);
        await _context.SaveChangesAsync();
        _main = main.Id;
        _store = store.Id;

        Product product = Product.Create("TRF-01", "Cola", CategoryId.New(), UnitOfMeasureId.New(), Actor).Value;
        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        _coke = product.Id;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task NewTransfer_AnnouncesBothEnds_OnceCommitted()
    {
        _context.Transfers.Add(Draft());
        await _context.SaveChangesAsync(CancellationToken.None);

        _published.Batches.Should().ContainSingle().Which.Should().BeEquivalentTo([_main, _store]);
    }

    [Fact]
    public async Task StatusChangeInsideATransaction_IsAnnouncedOnlyWhenItCommits()
    {
        await SeedDraftAsync();

        await using (IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync())
        {
            Transfer tracked = await _context.Transfers
                .Include(t => t.CustodyEvents)
                .SingleAsync(
                    t => t.SourceLocationId == _main && t.Status == TransferStatus.Draft,
                    CancellationToken.None);
            tracked.Submit(Actor, Now).IsSuccess.Should().BeTrue();
            await _context.SaveChangesAsync(CancellationToken.None);

            _published.Batches.Should().BeEmpty(because: "a reader told now would still see the old status");

            await transaction.CommitAsync();
        }

        _published.Batches.Should().ContainSingle().Which.Should().BeEquivalentTo([_main, _store]);
    }

    [Fact]
    public async Task RolledBackStatusChange_IsNeverAnnounced()
    {
        await SeedDraftAsync();

        await using (IDbContextTransaction transaction = await _context.Database.BeginTransactionAsync())
        {
            Transfer tracked = await _context.Transfers
                .Include(t => t.CustodyEvents)
                .SingleAsync(
                    t => t.SourceLocationId == _main && t.Status == TransferStatus.Draft,
                    CancellationToken.None);
            tracked.Submit(Actor, Now).IsSuccess.Should().BeTrue();
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

    private Transfer Draft() => Transfer.Create(
        _main, _store, [new TransferLineSpec(_coke, 10m)], Actor, Now).Value;

    private async Task SeedDraftAsync()
    {
        _context.Transfers.Add(Draft());
        await _context.SaveChangesAsync(CancellationToken.None);
        _published.Batches.Clear();
        _context.ChangeTracker.Clear();
    }

    private sealed class RecordingPublisher : ITransferChangePublisher
    {
        public List<LocationId[]> Batches { get; } = [];

        public Task PublishAsync(IReadOnlyCollection<LocationId> locations, CancellationToken cancellationToken)
        {
            Batches.Add([.. locations]);
            return Task.CompletedTask;
        }
    }
}
