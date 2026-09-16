using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Inventory;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Inventory;

/// <summary>
/// The low-stock read against a relational provider, so the id and state
/// filters must translate to SQL.
/// </summary>
public sealed class LowStockRepositoryTests : IAsyncLifetime
{
    private static readonly UserId Actor = UserId.New();

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private LowStockRepository _repository = null!;
    private Location _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _context = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();

        _store = Location.Create(Organization.DefaultId, "STORE01", "Store One", LocationKind.Store, "Asia/Manila").Value;
        _context.Locations.Add(_store);
        await _context.SaveChangesAsync();

        _repository = new LowStockRepository(_context);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetLowStockAsync_SumsAvailableBatchesAndReturnsItemsAtOrBelowTheReorderPoint()
    {
        Product atReorder = await SeedProductAsync("LOW-01", "At reorder");
        Product healthy = await SeedProductAsync("LOW-02", "Healthy");
        Product empty = await SeedProductAsync("LOW-03", "No balance rows");

        await SeedSettingAsync(atReorder.Id, minimum: 5m, reorder: 10m);
        await SeedSettingAsync(healthy.Id, minimum: 5m, reorder: 10m);
        await SeedSettingAsync(empty.Id, minimum: 5m, reorder: 10m);

        await SeedBalanceAsync(atReorder.Id, BatchId.New(), InventoryState.Available, 4m);
        await SeedBalanceAsync(atReorder.Id, BatchId.New(), InventoryState.Available, 6m);
        await SeedBalanceAsync(atReorder.Id, BatchId.Empty, InventoryState.Reserved, 50m);
        await SeedBalanceAsync(healthy.Id, BatchId.Empty, InventoryState.Available, 10.001m);

        IReadOnlyList<LowStockItem> items = await _repository.GetLowStockAsync(CancellationToken.None);

        items.Should().HaveCount(2);
        LowStockItem low = items.Should().ContainSingle(i => i.ProductId == atReorder.Id).Subject;
        low.Available.Should().Be(10m);
        low.ProductName.Should().Be("At reorder");
        low.LocationId.Should().Be(_store.Id);
        items.Should().ContainSingle(i => i.ProductId == empty.Id).Which.Available.Should().Be(0m);
    }

    [Fact]
    public async Task GetLowStockAsync_SkipsUnconfiguredUnstockedAndInactiveProducts()
    {
        Product unconfigured = await SeedProductAsync("SKIP-01", "Zero thresholds");
        Product unstocked = await SeedProductAsync("SKIP-02", "Not stocked");
        Product inactive = await SeedProductAsync("SKIP-03", "Discontinued");
        inactive.Deactivate(new DateOnly(2026, 9, 1)).IsSuccess.Should().BeTrue();
        await _context.SaveChangesAsync();

        await SeedSettingAsync(unconfigured.Id, minimum: 0m, reorder: 0m);
        await SeedSettingAsync(unstocked.Id, minimum: 5m, reorder: 10m, isStocked: false);
        await SeedSettingAsync(inactive.Id, minimum: 5m, reorder: 10m);

        IReadOnlyList<LowStockItem> items = await _repository.GetLowStockAsync(CancellationToken.None);

        items.Should().BeEmpty();
    }

    private async Task<Product> SeedProductAsync(string sku, string name)
    {
        Product product = Product.Create(sku, name, CategoryId.New(), UnitOfMeasureId.New(), Actor).Value;
        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        return product;
    }

    private async Task SeedSettingAsync(ProductId productId, decimal minimum, decimal reorder, bool isStocked = true)
    {
        ProductLocationSetting setting = ProductLocationSetting.Create(
                productId, _store.Id, isStocked, minimum, reorder, targetStock: 30m, maximumStock: 40m,
                preferredReplenishmentQuantity: 0m)
            .Value;

        _context.ProductLocationSettings.Add(setting);
        await _context.SaveChangesAsync();
    }

    private async Task SeedBalanceAsync(ProductId productId, BatchId batchKey, InventoryState state, decimal quantity)
    {
        InventoryBalance balance = InventoryBalance.CreateEmpty(_store.Id, productId, batchKey, state);
        _context.InventoryBalances.Add(balance);

        // Seeded directly: this pins the read query, not the posting rules.
        _context.Entry(balance).Property(b => b.Quantity).CurrentValue = quantity;

        await _context.SaveChangesAsync();
    }
}
