using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Pos.Application.Common.Abstractions;
using Pos.Application.Inventory;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Inventory;

/// <summary>
/// The expiry reads against a real relational provider. The batch-key filter
/// must translate to SQL: an unmapped member such as <c>BatchId.IsEmpty</c>
/// compiles but throws at query time, which only a provider-backed test sees.
/// </summary>
public sealed class ExpiryServiceQueryTests : IAsyncLifetime
{
    private static readonly LocationId Store = LocationId.New();
    private static readonly UserId Actor = UserId.New();

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private ExpiryService _service = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _context = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();

        IExpiryRepository repository = Substitute.For<IExpiryRepository>();
        repository.GetExpiryWarningDaysAsync(Store, Arg.Any<CancellationToken>()).Returns(90);

        _service = new ExpiryService(
            _context,
            repository,
            Substitute.For<IInventoryLedger>(),
            Substitute.For<IDocumentNumberGenerator>(),
            Substitute.For<ISystemClock>());
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetExpiredBatchesAsync_ReturnsPastExpiryAvailableBuckets_Only()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Product product = await SeedProductAsync();

        Batch expired = await SeedBatchAsync(product.Id, "LOT-OLD", today.AddDays(-10));
        Batch current = await SeedBatchAsync(product.Id, "LOT-NEW", today.AddDays(30));

        await SeedBalanceAsync(product.Id, expired.Id, 5m);
        await SeedBalanceAsync(product.Id, current.Id, 7m);
        await SeedBalanceAsync(product.Id, BatchId.Empty, 9m);

        IReadOnlyList<ExpiredBatchItem> items = await _service.GetExpiredBatchesAsync(Store, CancellationToken.None);

        ExpiredBatchItem item = items.Should().ContainSingle().Subject;
        item.BatchId.Should().Be(expired.Id);
        item.Quantity.Should().Be(5m);
    }

    [Fact]
    public async Task GetExpiringBatchesAsync_ReturnsBucketsWithinTheWarningHorizon()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Product product = await SeedProductAsync();

        Batch soon = await SeedBatchAsync(product.Id, "LOT-SOON", today.AddDays(30));
        Batch far = await SeedBatchAsync(product.Id, "LOT-FAR", today.AddDays(200));

        await SeedBalanceAsync(product.Id, soon.Id, 4m);
        await SeedBalanceAsync(product.Id, far.Id, 6m);
        await SeedBalanceAsync(product.Id, BatchId.Empty, 9m);

        IReadOnlyList<ExpiringBatchSummary> items = await _service.GetExpiringBatchesAsync(Store, CancellationToken.None);

        ExpiringBatchSummary item = items.Should().ContainSingle().Subject;
        item.BatchId.Should().Be(soon.Id);
        item.DaysUntilExpiry.Should().Be(30);
    }

    private async Task<Product> SeedProductAsync()
    {
        Product product = Product.Create(
                "EXP-01", "Expiring", CategoryId.New(), UnitOfMeasureId.New(), Actor,
                tracksBatches: true, tracksExpiry: true, shelfLifeDays: 365)
            .Value;

        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        return product;
    }

    private async Task<Batch> SeedBatchAsync(ProductId productId, string lot, DateOnly expiresOn)
    {
        Batch batch = Batch.Create(
                productId, SupplierId.New(), lot, expiresOn.AddDays(-400), manufacturedOn: null, expiresOn,
                unitCost: 10m, Actor, DateTimeOffset.UtcNow)
            .Value;

        _context.Batches.Add(batch);
        await _context.SaveChangesAsync();
        return batch;
    }

    private async Task SeedBalanceAsync(ProductId productId, BatchId batchKey, decimal quantity)
    {
        InventoryBalance balance = InventoryBalance.CreateEmpty(Store, productId, batchKey, InventoryState.Available);
        _context.InventoryBalances.Add(balance);

        // Seeded directly: this pins the read queries, not the posting rules.
        _context.Entry(balance).Property(b => b.Quantity).CurrentValue = quantity;
        _context.Entry(balance).Property(b => b.AverageUnitCost).CurrentValue = 10m;

        await _context.SaveChangesAsync();
    }
}
