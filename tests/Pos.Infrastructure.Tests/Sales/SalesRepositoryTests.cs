using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Sales;

/// <summary>
/// Repository behaviour against a real relational provider: the sale is saved
/// with its lines and payments atomically, and every read serves the facts the
/// completion handler re-derives from.
/// </summary>
/// <remarks>
/// Runs on SQLite in memory so it executes anywhere, including a machine without
/// Docker. The PostgreSQL guards are separate; this suite proves the mapping,
/// the tracking/non-tracking split, the price <c>Include</c>, and the expired
/// batch fence.
/// </remarks>
public sealed class SalesRepositoryTests : IAsyncLifetime
{
    private static readonly LocationId Main = LocationId.New();
    private static readonly UserId Cashier = UserId.New();
    private static readonly CategoryId Category = CategoryId.New();
    private static readonly UnitOfMeasureId Unit = UnitOfMeasureId.New();
    private static readonly ProductId Widget = ProductId.New();

    private SqliteConnection _connection = null!;
    private PosDbContext _context = null!;
    private SalesRepository _repository = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new PosDbContext(options);
        await _context.Database.EnsureCreatedAsync();

        _repository = new SalesRepository(_context);

        // Every test may read location facts: one Main warehouse, one Store, and
        // the EXT-CUSTOMER counterparty the sold goods are posted to.
        if (Location.Create(Organization.DefaultId, "MAIN", "Main Warehouse", LocationKind.MainWarehouse, "Asia/Manila")
                is { IsSuccess: true } main
            && Location.Create(Organization.DefaultId, "STORE01", "Store One", LocationKind.Store, "Asia/Manila")
                is { IsSuccess: true } store
            && Location.CreateSystemExternal(Organization.DefaultId, SystemLocationCodes.ExternalCustomer, "External Customer")
                is { IsSuccess: true } customer)
        {
            _context.Locations.Add(main.Value);
            _context.Locations.Add(store.Value);
            _context.Locations.Add(customer.Value);
            await _context.SaveChangesAsync();
        }
        else
        {
            throw new InvalidOperationException("Could not seed test locations.");
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AddAsync_PersistsSaleLinesAndPayments_AndReturnsTheId()
    {
        Sale sale = NewSale(amount: 200m, unitCount: 2, unitPrice: 100m);

        Result<SaleId> saved = await _repository.AddAsync(sale, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(sale.Id);

        Sale stored = await _context.Sales
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .SingleAsync(s => s.Id == sale.Id, CancellationToken.None);

        stored.Number.Should().Be(sale.Number);
        stored.Status.Should().Be(SaleStatus.Completed);
        stored.EventId.Should().Be(sale.EventId);
        stored.BusinessDate.Should().Be(sale.BusinessDate);
        stored.NetTotal.Should().Be(200m);
        stored.Items.Should().HaveCount(1);
        stored.Items[0].ProductName.Should().Be("Widget");
        stored.Payments.Should().HaveCount(1);
        stored.Payments[0].Method.Should().Be(PaymentMethod.Cash);
        stored.Payments[0].Amount.Should().Be(200m);
    }

    [Fact]
    public async Task AddAsync_RoundTripsAPriceOverride_WithItsAuthorizer()
    {
        Sale sale = NewSaleWithOverride(amount: 100m);

        Result<SaleId> saved = await _repository.AddAsync(sale, CancellationToken.None);

        saved.IsSuccess.Should().BeTrue();

        Sale stored = await _context.Sales
            .Include(s => s.Items)
            .SingleAsync(s => s.Id == sale.Id, CancellationToken.None);

        stored.Items.Single().PriceWasOverridden.Should().BeTrue();
        stored.Items.Single().PriceOverrideAuthorizedByUserId.Should().Be(Cashier);
    }

    [Fact]
    public async Task GetLocationAsync_ExistingLocation_ReturnsKindAndSettings()
    {
        LocationSettings settings = new() { NegativeStockPolicy = NegativeStockPolicy.AllowWithPermission };

        _context.Entry((await _context.Locations.SingleAsync(l => l.Code == "STORE01", CancellationToken.None)))
            .Property(l => l.Settings).CurrentValue = settings;
        await _context.SaveChangesAsync();

        SaleLocationFacts? facts = await _repository.GetLocationAsync(
            (await _context.Locations.SingleAsync(l => l.Code == "STORE01", CancellationToken.None)).Id,
            CancellationToken.None);

        facts.Should().NotBeNull();
        facts!.Kind.Should().Be(LocationKind.Store);
        facts.Settings.NegativeStockPolicy.Should().Be(NegativeStockPolicy.AllowWithPermission);
    }

    [Fact]
    public async Task GetLocationAsync_MissingLocation_ReturnsNull()
    {
        SaleLocationFacts? facts = await _repository.GetLocationAsync(LocationId.New(), CancellationToken.None);

        facts.Should().BeNull();
    }

    [Fact]
    public async Task GetSaleProductsAsync_ReturnsPriceRows_ForTheRequestedProducts()
    {
        Product product = NewPricedProduct();

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        IReadOnlyList<Product> loaded = await _repository.GetSaleProductsAsync([product.Id], CancellationToken.None);

        Product found = loaded.Should().ContainSingle(p => p.Id == product.Id).Subject;
        found.Prices.Should().ContainSingle();
        found.Prices[0].Amount.Should().Be(100m);
    }

    [Fact]
    public async Task GetSaleProductsAsync_EmptyRequest_ReturnsEmpty()
    {
        IReadOnlyList<Product> loaded = await _repository.GetSaleProductsAsync([], CancellationToken.None);

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSaleProductsAsync_MissingProduct_IsOmitted()
    {
        Product product = NewPricedProduct();

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        IReadOnlyList<Product> loaded = await _repository.GetSaleProductsAsync(
            [product.Id, ProductId.New()],
            CancellationToken.None);

        loaded.Should().ContainSingle(p => p.Id == product.Id);
    }

    [Fact]
    public async Task GetExternalCustomerLocationIdAsync_ReturnsTheProvisionedCounterparty()
    {
        LocationId? customerId = await _repository.GetExternalCustomerLocationIdAsync(CancellationToken.None);

        customerId.Should().NotBeNull();
        (await _context.Locations.SingleAsync(l => l.Id == customerId!.Value, CancellationToken.None))
            .Code.Should().Be(SystemLocationCodes.ExternalCustomer);
    }

    [Fact]
    public async Task GetExpiredAvailableBatchesAsync_ReturnsOnlyBatches_PastTheBusinessDate()
    {
        Product product = NewBatchTrackedProduct();

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        Batch stale = await NewBatchAsync(product.Id, "LOT-STALE", new DateOnly(2026, 8, 1), 40m);
        Batch fresh = await NewBatchAsync(product.Id, "LOT-FRESH", new DateOnly(2026, 10, 1), 55m);

        await SeedBalanceAsync(product.Id, stale.Id, InventoryState.Available, 12m, 40m);
        await SeedBalanceAsync(product.Id, fresh.Id, InventoryState.Available, 6m, 55m);

        // The business date is the fence: the batch expiring on the date itself
        // was still sellable that day and stays out of the expired path.
        Batch boundary = await NewBatchAsync(product.Id, "LOT-BOUNDARY", new DateOnly(2026, 9, 14), 60m);

        await SeedBalanceAsync(product.Id, boundary.Id, InventoryState.Available, 4m, 60m);

        IReadOnlyList<ExpiredSaleBatch> expired = await _repository.GetExpiredAvailableBatchesAsync(
            Main, product.Id, new DateOnly(2026, 9, 14), CancellationToken.None);

        expired.Should().ContainSingle();
        expired[0].BatchId.Should().Be(stale.Id);
        expired[0].LotNumber.Should().Be("LOT-STALE");
        expired[0].Quantity.Should().Be(12m);
        expired[0].ExpiresOn.Should().Be(new DateOnly(2026, 8, 1));
        expired[0].UnitCost.Should().Be(40m);
    }

    [Fact]
    public async Task GetExpiredAvailableBatchesAsync_IgnoresBucketlessHoldings_AndNonSellableStates()
    {
        Product product = NewBatchTrackedProduct();
        Product loose = NewProduct();

        _context.Products.Add(product);
        _context.Products.Add(loose);
        await _context.SaveChangesAsync();

        Batch stale = await NewBatchAsync(product.Id, "LOT-STALE", new DateOnly(2026, 8, 1), 40m);

        // Available but bucketless (not batch tracked at seed) — never expired.
        await SeedBalanceAsync(product.Id, BatchId.Empty, InventoryState.Available, 9m, 25m);
        // Quarantined, even past expiry, is not sellable.
        await SeedBalanceAsync(product.Id, stale.Id, InventoryState.Quarantine, 5m, 40m);
        // Zero quantity is not a bucket to consume.
        await SeedBalanceAsync(product.Id, stale.Id, InventoryState.Available, 0m, 40m);

        IReadOnlyList<ExpiredSaleBatch> expired = await _repository.GetExpiredAvailableBatchesAsync(
            Main, product.Id, new DateOnly(2026, 9, 14), CancellationToken.None);

        expired.Should().BeEmpty();
    }

    [Fact]
    public async Task GetExpiredAvailableBatchesAsync_OrdersByExpiry_ThenById()
    {
        Product product = NewBatchTrackedProduct();

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        Batch later = await NewBatchAsync(product.Id, "LOT-LATER", new DateOnly(2026, 9, 1), 40m);
        Batch earlier = await NewBatchAsync(product.Id, "LOT-EARLIER", new DateOnly(2026, 7, 1), 40m);

        await SeedBalanceAsync(product.Id, earlier.Id, InventoryState.Available, 3m, 40m);
        await SeedBalanceAsync(product.Id, later.Id, InventoryState.Available, 3m, 40m);

        IReadOnlyList<ExpiredSaleBatch> expired = await _repository.GetExpiredAvailableBatchesAsync(
            Main, product.Id, new DateOnly(2026, 9, 14), CancellationToken.None);

        expired.Should().HaveCount(2);
        expired[0].BatchId.Should().Be(earlier.Id, because: "FEFO consumes the nearest expiry first");
        expired[1].BatchId.Should().Be(later.Id);
    }

    private static Sale NewSale(decimal amount, int unitCount, decimal unitPrice)
    {
        ItemSpec item = new(
            Widget,
            "Widget",
            Barcode: null,
            unitCount,
            Unit,
            unitPrice,
            PriceVersion: ProductPriceId.New(),
            PriceWasOverridden: false,
            PriceOverrideAuthorizedByUserId: null,
            Discount: 0m,
            DiscountAuthorizedByUserId: null,
            VatRate: null,
            IsVatExempt: false,
            IsZeroRated: true,
            BatchId: null,
            BatchCode: null,
            BatchExpiresOn: null,
            UnitCost: 0m,
            TracksBatches: false);

        PaymentSpec payment = new(PaymentMethod.Cash, amount, amount, ProviderReference: null);

        return Sale.Create(
                DocumentNumber.FromTrustedSource("SAL-2026-000001"),
                EventId.New(),
                Main,
                CashierShiftId.New(),
                DeviceId.New(),
                customerId: null,
                new DateOnly(2026, 9, 14),
                new DateTimeOffset(2026, 9, 14, 7, 0, 0, TimeSpan.Zero),
                Cashier,
                [item],
                [payment])
            .Value;
    }

    private static Sale NewSaleWithOverride(decimal amount)
    {
        ItemSpec item = new(
            Widget,
            "Widget",
            Barcode: null,
            Quantity: 1m,
            Unit,
            amount,
            PriceVersion: ProductPriceId.New(),
            PriceWasOverridden: true,
            PriceOverrideAuthorizedByUserId: Cashier,
            Discount: 0m,
            DiscountAuthorizedByUserId: null,
            VatRate: null,
            IsVatExempt: false,
            IsZeroRated: true,
            BatchId: null,
            BatchCode: null,
            BatchExpiresOn: null,
            UnitCost: 0m,
            TracksBatches: false);

        PaymentSpec payment = new(PaymentMethod.Cash, amount, amount, ProviderReference: null);

        return Sale.Create(
                DocumentNumber.FromTrustedSource("SAL-2026-000002"),
                EventId.New(),
                Main,
                CashierShiftId.New(),
                DeviceId.New(),
                customerId: null,
                new DateOnly(2026, 9, 14),
                new DateTimeOffset(2026, 9, 14, 7, 0, 0, TimeSpan.Zero),
                Cashier,
                [item],
                [payment])
            .Value;
    }

    private static Product NewProduct()
        => Product.Create("WIDGET-01", "Widget", Category, Unit, Cashier).Value;

    private static Product NewPricedProduct()
    {
        Product product = NewProduct();
        product.SchedulePrice(
            locationId: null,
            amount: 100m,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            effectiveToUtc: null,
            Cashier,
            reason: "Launch",
            nowUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return product;
    }

    private static Product NewBatchTrackedProduct()
        => Product.Create("WIDGET-B-01", "Widget Batch", Category, Unit, Cashier, tracksBatches: true, tracksExpiry: true, shelfLifeDays: 365)
            .Value;

    private async Task<Batch> NewBatchAsync(ProductId productId, string lot, DateOnly expiresOn, decimal unitCost)
    {
        SupplierId supplierId = SupplierId.New();

        Result<Batch> created = Batch.Create(
            productId,
            supplierId,
            lot,
            receivedOn: new DateOnly(2026, 1, 10),
            manufacturedOn: null,
            expiresOn,
            unitCost,
            Cashier,
            now: new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));

        if (created.IsFailure)
        {
            throw new InvalidOperationException("Could not create test batch.");
        }

        _context.Batches.Add(created.Value);
        await _context.SaveChangesAsync();
        return created.Value;
    }

    private async Task SeedBalanceAsync(
        ProductId productId,
        BatchId batchKey,
        InventoryState state,
        decimal quantity,
        decimal unitCost)
    {
        InventoryBalance balance = InventoryBalance.CreateEmpty(Main, productId, batchKey, state);

        _context.InventoryBalances.Add(balance);

        // Introduce the seed state without going through the ledger: this test
        // pins the repository's read fence, not the posting rules.
        if (quantity != 0m)
        {
            _context.Entry(balance).Property(b => b.Quantity).CurrentValue = quantity;
            _context.Entry(balance).Property(b => b.AverageUnitCost).CurrentValue = unitCost;
        }

        await _context.SaveChangesAsync();
    }
}