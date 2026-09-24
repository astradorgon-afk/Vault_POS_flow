using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>What the development seeder created or found already present.</summary>
/// <param name="LocationsCreated">Physical and external locations created.</param>
/// <param name="ProductsCreated">Products created.</param>
/// <param name="PricesCreated">Selling prices created.</param>
/// <param name="AccountsCreated">Staff accounts created, when account seeding is enabled.</param>
/// <param name="StockGroupsCreated">Opening-balance inventory events created.</param>
public sealed record DevelopmentDataSummary(
    int LocationsCreated,
    int ProductsCreated,
    int PricesCreated,
    int AccountsCreated,
    int StockGroupsCreated)
{
    /// <summary>The summary when seeding was not requested.</summary>
    public static DevelopmentDataSummary None { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Seeds the development database with a usable topology: the three system
/// counterparties, one Main Warehouse, three stores, reference master data, a
/// small product catalogue, opening stock balances at every physical location
/// and — when explicitly enabled — staff accounts with a known password.
/// </summary>
/// <remarks>
/// <para>
/// Everything is idempotent: each run looks up what already exists and creates
/// only the gaps, so restarting a development host never duplicates or resets
/// data. The master data is written inside one transaction.
/// </para>
/// <para>
/// The whole seeder is governed by <c>Database:SeedDevelopmentData</c>, which is
/// true in the Development configuration and false everywhere else. The staff
/// accounts additionally need <c>Seeding:EnableDevelopmentAccounts</c>, because
/// a known password is a credential that must never leak into a shared or
/// staging database by accident.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="users">Identity's user manager.</param>
/// <param name="ledger">The inventory ledger used to post opening balances.</param>
/// <param name="database">Database configuration.</param>
/// <param name="seeding">Seeding configuration.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="logger">Logger.</param>
public sealed class DevelopmentDataSeeder(
    PosDbContext context,
    UserManager<AppUser> users,
    IInventoryLedger ledger,
    IOptions<DatabaseOptions> database,
    IOptions<SeedingOptions> seeding,
    ISystemClock clock,
    ILogger<DevelopmentDataSeeder> logger)
{
    /// <summary>The password every development account shares. Development only,
    /// and deliberately easy to type on a store register. It must never be used
    /// outside a development database.</summary>
    private const string DevelopmentPassword = "cash1234";

    /// <summary>Development accounts created by older seeds with the per-store
    /// naming convention. Databases seeded before the credential set was
    /// simplified keep them; they are retired (not created) on every run so the
    /// login list stays short with no manual cleanup.</summary>
    private static readonly string[] ObsoleteDevelopmentAccounts =
    [
        "main.manager", "inv.staff",
        "s1.manager", "s2.manager", "s3.manager",
        "s1.cashier", "s2.cashier", "s3.cashier",
    ];

    private readonly IOptions<DatabaseOptions> _database = database;
    private readonly IOptions<SeedingOptions> _seeding = seeding;

    private int _locationsCreated;
    private int _productsCreated;
    private int _pricesCreated;
    private int _accountsCreated;
    private int _stockGroupsCreated;

    /// <summary>Seeds the development data when configured.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what was created.</returns>
    public async Task<DevelopmentDataSummary> SeedAsync(CancellationToken cancellationToken)
    {
        if (!_database.Value.SeedDevelopmentData)
        {
            return DevelopmentDataSummary.None;
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await SeedExternalLocationsAsync(cancellationToken).ConfigureAwait(false);
        await SeedPhysicalLocationsAsync(cancellationToken).ConfigureAwait(false);
        await SeedReferenceMasterDataAsync(cancellationToken).ConfigureAwait(false);

        // Stock seeding looks products up by SKU from the database, so products
        // created above must be written first. The transaction still makes the
        // whole seed all-or-nothing.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SeedStockAsync(cancellationToken).ConfigureAwait(false);
        await SeedLocationSettingsAsync(cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (_seeding.Value.EnableDevelopmentAccounts)
        {
            await SeedAccountsAsync(cancellationToken).ConfigureAwait(false);

            // A customer records who created it, so the demo customers wait
            // for the development owner account to exist.
            await SeedCustomersAsync(cancellationToken).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Development seed complete: {Locations} locations, {Products} products, {Prices} prices, " +
                "{StockGroups} stock events, {Accounts} accounts.",
                _locationsCreated,
                _productsCreated,
                _pricesCreated,
                _stockGroupsCreated,
                _accountsCreated);
        }

        return new DevelopmentDataSummary(_locationsCreated, _productsCreated, _pricesCreated, _accountsCreated, _stockGroupsCreated);
    }

    private async Task SeedExternalLocationsAsync(CancellationToken cancellationToken)
    {
        foreach (string code in SystemLocationCodes.All)
        {
            if (await LocationExistsAsync(code, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            Result<Location> created = Location.CreateSystemExternal(
                Organization.DefaultId,
                code,
                code switch
                {
                    SystemLocationCodes.ExternalSupplier => "External Supplier",
                    SystemLocationCodes.ExternalCustomer => "External Customer",
                    _ => "External Write-off",
                });

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.Locations.Add(created.Value);
            _locationsCreated++;
        }
    }

    private async Task SeedPhysicalLocationsAsync(CancellationToken cancellationToken)
    {
        (string Code, string Name, LocationKind Kind)[] topologies =
        [
            ("MAIN", "Main Warehouse", LocationKind.MainWarehouse),
            ("STORE01", "Store One", LocationKind.Store),
            ("STORE02", "Store Two", LocationKind.Store),
            ("STORE03", "Store Three", LocationKind.Store),
        ];

        foreach ((string code, string name, LocationKind kind) in topologies)
        {
            if (await LocationExistsAsync(code, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            Result<Location> created = Location.Create(
                Organization.DefaultId,
                code,
                name,
                kind,
                "Asia/Manila");

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.Locations.Add(created.Value);
            _locationsCreated++;
        }
    }

    private async Task SeedReferenceMasterDataAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, UnitOfMeasureId> uomByCode = await EnsureUnitsOfMeasureAsync(cancellationToken);
        Dictionary<string, CategoryId> categoryByCode = await EnsureCategoriesAsync(cancellationToken);
        Dictionary<string, BrandId> brandByName = await EnsureBrandsAsync(cancellationToken);
        Dictionary<string, SupplierId> supplierByCode = await EnsureSuppliersAsync(cancellationToken);

        DateTimeOffset historyStart = HistoryStartUtc;

        foreach ((string sku, string name, string category, string brand, string supplier, string unit, string barcode, decimal price,
                  decimal unitCost, _, _) in Catalogue)
        {
            Product? product = await context.Products
                .Include(p => p.Prices)
                .FirstOrDefaultAsync(p => p.Sku == Sku.FromTrustedSource(sku), cancellationToken)
                .ConfigureAwait(false);

            if (product is null)
            {
                Result<Product> productResult = Product.Create(
                    sku,
                    name,
                    categoryByCode[category],
                    uomByCode[unit],
                    UserId.Empty,
                    brandId: brandByName[brand],
                    primarySupplierId: supplierByCode[supplier],
                    defaultPurchaseCost: unitCost);

                if (productResult.IsFailure)
                {
                    throw new InvalidOperationException(productResult.Error.Message);
                }

                product = productResult.Value;

                Result barcodeAttached = product.AddBarcode(
                    barcode,
                    uomByCode[unit],
                    packQuantity: 1m,
                    isPrimary: true,
                    UserId.Empty,
                    requireChecksum: false);

                if (barcodeAttached.IsFailure)
                {
                    throw new InvalidOperationException(barcodeAttached.Error.Message);
                }

                context.Products.Add(product);
                _productsCreated++;
            }

            // The demo sales history starts at HistoryStartUtc, and a sale is
            // priced at the moment it completed, so every product needs a
            // business-wide price in effect from then. A product priced later
            // (seeded by an older run) gets an earlier row that ends exactly
            // where its first price begins, so the two never overlap.
            bool pricedAtHistoryStart = product.Prices.Any(p =>
                p.LocationId is null
                && p.EffectiveFromUtc <= historyStart
                && (p.EffectiveToUtc is null || p.EffectiveToUtc > historyStart));

            if (!pricedAtHistoryStart)
            {
                DateTimeOffset? firstLaterPrice = product.Prices
                    .Where(p => p.LocationId is null && p.EffectiveFromUtc > historyStart)
                    .Select(p => (DateTimeOffset?)p.EffectiveFromUtc)
                    .Min();

                // The seeder writes history as of its start, so the backdating
                // guard is evaluated against that instant rather than today.
                Result<ProductPriceId> priceScheduled = product.SchedulePrice(
                    locationId: null,
                    amount: price,
                    effectiveFromUtc: historyStart,
                    effectiveToUtc: firstLaterPrice,
                    createdByUserId: UserId.Empty,
                    reason: "Development catalogue price.",
                    nowUtc: historyStart);

                if (priceScheduled.IsFailure)
                {
                    throw new InvalidOperationException(priceScheduled.Error.Message);
                }

                // Prices are exposed by the aggregate as a defensive copy. Add
                // the newly scheduled row explicitly so EF tracks it even when
                // the product itself was already present and tracked.
                context.ProductPrices.Add(product.Prices.Single(p => p.Id == priceScheduled.Value));
                _pricesCreated++;
            }
        }
    }

    /// <summary>How far back the demo history reaches. Prices and opening
    /// balances start here so the activity <c>tools/Pos.DemoData</c> posts over
    /// the last thirty days (sales, returns, voids) always finds a price in
    /// effect and stock on hand.</summary>
    private const int HistoryDays = 35;

    private DateTimeOffset HistoryStartUtc
    {
        get
        {
            DateTimeOffset today = clock.UtcNow;
            return new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-HistoryDays);
        }
    }

    /// <summary>The demo catalogue: identity, selling price, acquisition cost and
    /// the opening stock each store and the Main Warehouse hold. A few store
    /// quantities are deliberately low so replenishment has work to show.</summary>
    private static readonly (string Sku, string Name, string Category, string Brand, string Supplier, string Unit,
        string Barcode, decimal Price, decimal UnitCost, decimal StoreOnHand, decimal WarehouseOnHand)[] Catalogue =
    [
        ("RICE-01", "Premium Rice 5kg", "GROCERIES", "Green Valley", "SUP1", "KG", "4800000000017", 285m, 240m, 25m, 300m),
        ("COFFEE-200", "Ground Coffee 200g", "GROCERIES", "Sunrise", "SUP1", "KG", "4800000000024", 165m, 132m, 20m, 220m),
        ("SUGAR-1K", "Refined Sugar 1kg", "GROCERIES", "Green Valley", "SUP2", "KG", "4800000000031", 78m, 63m, 40m, 320m),
        ("OIL-1L", "Cooking Oil 1L", "GROCERIES", "Sunrise", "SUP2", "L", "4800000000048", 125m, 99m, 30m, 280m),
        ("MILK-370", "Evaporated Milk 370ml", "DAIRY", "Green Valley", "SUP1", "L", "4800000000055", 42m, 33m, 60m, 500m),
        ("EGGS-DZ", "Large Eggs (Dozen)", "DAIRY", "Green Valley", "SUP2", "PC", "4800000000062", 110m, 86m, 15m, 120m),
        ("WATER-500", "Mineral Water 500ml", "BEVERAGES", "Sunrise", "SUP1", "L", "4800000000079", 12m, 9m, 120m, 800m),
        ("SODA-1L", "Premium Soda 1L", "BEVERAGES", "Sunrise", "SUP2", "L", "4800000000086", 58m, 44m, 48m, 240m),
        ("DETER-400", "Laundry Powder 400g", "HOUSEHOLD", "Green Valley", "SUP1", "PC", "4800000000093", 135m, 106m, 36m, 200m),
        ("NOODLE-55", "Instant Noodles Chicken 55g", "GROCERIES", "Kitchen Best", "SUP1", "PC", "4800000001014", 16m, 11m, 200m, 1200m),
        ("TUNA-155", "Canned Tuna 155g", "GROCERIES", "Kitchen Best", "SUP1", "PC", "4800000001021", 42m, 33m, 120m, 600m),
        ("CBEEF-150", "Corned Beef 150g", "GROCERIES", "Kitchen Best", "SUP1", "PC", "4800000001038", 58m, 45m, 90m, 480m),
        ("PASTA-1K", "Spaghetti Pasta 1kg", "GROCERIES", "Golden Harvest", "SUP1", "PC", "4800000001045", 89m, 70m, 60m, 300m),
        ("TSAUCE-250", "Tomato Sauce 250g", "GROCERIES", "Golden Harvest", "SUP1", "PC", "4800000001052", 29m, 22m, 90m, 450m),
        ("SOY-1L", "Soy Sauce 1L", "GROCERIES", "Kitchen Best", "SUP2", "L", "4800000001069", 55m, 42m, 70m, 360m),
        ("VINEGAR-1L", "Cane Vinegar 1L", "GROCERIES", "Kitchen Best", "SUP2", "L", "4800000001076", 45m, 34m, 70m, 360m),
        ("SALT-500", "Iodized Salt 500g", "GROCERIES", "Green Valley", "SUP2", "PC", "4800000001083", 18m, 12m, 80m, 400m),
        ("OATS-800", "Rolled Oats 800g", "GROCERIES", "Golden Harvest", "SUP1", "PC", "4800000001090", 145m, 115m, 30m, 180m),
        ("OJ-1L", "Orange Juice 1L", "BEVERAGES", "Sunrise", "SUP3", "L", "4800000001106", 95m, 74m, 50m, 260m),
        ("ICETEA-500", "Iced Tea 500ml", "BEVERAGES", "Sunrise", "SUP3", "L", "4800000001113", 35m, 25m, 110m, 600m),
        ("ENERGY-250", "Energy Drink 250ml", "BEVERAGES", "Sunrise", "SUP3", "L", "4800000001120", 42m, 31m, 90m, 480m),
        ("COFFEE-3IN1", "3-in-1 Coffee Mix (10s)", "BEVERAGES", "Sunrise", "SUP3", "PC", "4800000001137", 72m, 56m, 80m, 420m),
        ("CHOCO-1L", "Chocolate Drink 1L", "BEVERAGES", "Green Valley", "SUP3", "L", "4800000001144", 88m, 68m, 40m, 220m),
        ("MILK-1L", "Fresh Milk 1L", "DAIRY", "Green Valley", "SUP2", "L", "4800000001151", 98m, 78m, 45m, 240m),
        ("CHEESE-165", "Cheddar Cheese 165g", "DAIRY", "Green Valley", "SUP2", "PC", "4800000001168", 76m, 58m, 40m, 200m),
        ("BUTTER-225", "Salted Butter 225g", "DAIRY", "Green Valley", "SUP2", "PC", "4800000001175", 135m, 108m, 10m, 150m),
        ("YOGURT-110", "Yogurt Cup 110g", "DAIRY", "Green Valley", "SUP2", "PC", "4800000001182", 32m, 23m, 60m, 300m),
        ("CHIPS-60", "Potato Chips 60g", "SNACKS", "Golden Harvest", "SUP1", "PC", "4800000001199", 38m, 27m, 120m, 600m),
        ("CHOCBAR-40", "Chocolate Bar 40g", "SNACKS", "Golden Harvest", "SUP1", "PC", "4800000001205", 45m, 33m, 100m, 500m),
        ("COOKIES-200", "Butter Cookies 200g", "SNACKS", "Golden Harvest", "SUP5", "PC", "4800000001212", 85m, 64m, 50m, 260m),
        ("PEANUT-100", "Roasted Peanuts 100g", "SNACKS", "Golden Harvest", "SUP1", "PC", "4800000001229", 28m, 19m, 90m, 450m),
        ("BREAD-LOAF", "Sliced Loaf Bread", "BAKERY", "Golden Harvest", "SUP5", "PC", "4800000001236", 72m, 55m, 40m, 160m),
        ("PANDESAL-10", "Pandesal (10 pcs)", "BAKERY", "Golden Harvest", "SUP5", "PC", "4800000001243", 50m, 36m, 12m, 120m),
        ("DISH-500", "Dishwashing Liquid 500ml", "HOUSEHOLD", "Pure Living", "SUP4", "PC", "4800000001250", 68m, 50m, 60m, 300m),
        ("BLEACH-1L", "Bleach 1L", "HOUSEHOLD", "Pure Living", "SUP4", "L", "4800000001267", 48m, 35m, 50m, 260m),
        ("TISSUE-4", "Bathroom Tissue (4 rolls)", "HOUSEHOLD", "Pure Living", "SUP4", "PC", "4800000001274", 95m, 72m, 60m, 320m),
        ("TRASH-10", "Garbage Bags (10s)", "HOUSEHOLD", "Pure Living", "SUP4", "PC", "4800000001281", 55m, 40m, 50m, 260m),
        ("SOAP-135", "Bath Soap 135g", "PERSONAL", "Pure Living", "SUP4", "PC", "4800000001298", 42m, 31m, 90m, 480m),
        ("SHAMPOO-340", "Shampoo 340ml", "PERSONAL", "Pure Living", "SUP4", "PC", "4800000001304", 165m, 128m, 30m, 180m),
        ("TPASTE-150", "Toothpaste 150g", "PERSONAL", "Pure Living", "SUP4", "PC", "4800000001311", 98m, 75m, 45m, 240m),
        ("ALCOHOL-500", "Isopropyl Alcohol 500ml", "PERSONAL", "Pure Living", "SUP4", "PC", "4800000001328", 85m, 62m, 8m, 200m),
        ("HOTDOG-1K", "Jumbo Hotdog 1kg", "FROZEN", "Kitchen Best", "SUP2", "PC", "4800000001335", 210m, 168m, 25m, 140m),
        ("NUGGETS-500", "Chicken Nuggets 500g", "FROZEN", "Kitchen Best", "SUP2", "PC", "4800000001342", 175m, 138m, 6m, 120m),
    ];

    private async Task SeedStockAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, (LocationId Id, LocationKind Kind)> locations = await context.Locations
            .AsNoTracking()
            .Where(l => l.Code == SystemLocationCodes.ExternalSupplier
                        || l.Code == MainLocationCode
                        || l.Code == StoreOneCode
                        || l.Code == StoreTwoCode
                        || l.Code == StoreThreeCode)
            .Select(l => new { l.Code, l.Id, l.Kind })
            .ToDictionaryAsync(l => l.Code, l => (l.Id, l.Kind), cancellationToken)
            .ConfigureAwait(false);

        if (!locations.TryGetValue(SystemLocationCodes.ExternalSupplier, out (LocationId Id, LocationKind Kind) external))
        {
            throw new InvalidOperationException("The external supplier location is missing for stock seeding.");
        }

        // Opening balances date from the start of the demo history, so the
        // backdated sales the demo tool posts draw on stock already present.
        DateTimeOffset openedAt = HistoryStartUtc;
        DateOnly businessDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(openedAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila")).DateTime);

        foreach ((string sku, _, _, _, _, _, _, _, decimal unitCost, decimal storeOnHand, decimal warehouseOnHand) in Catalogue)
        {
            ProductId productId = await context.Products
                .AsNoTracking()
                .Where(p => p.Sku == Sku.FromTrustedSource(sku))
                .Select(p => p.Id)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach ((string code, decimal quantity) in new[]
                     {
                         (MainLocationCode, warehouseOnHand),
                         (StoreOneCode, storeOnHand),
                         (StoreTwoCode, storeOnHand),
                         (StoreThreeCode, storeOnHand),
                     })
            {
                (LocationId id, LocationKind kind) = locations[code];

                bool alreadySeeded = await context.InventoryMovements
                    .AsNoTracking()
                    .AnyAsync(
                        m => m.MovementType == InventoryMovementType.OpeningBalance
                             && m.ProductId == productId
                             && m.LocationId == id
                             && m.QuantityDelta > 0m,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (alreadySeeded)
                {
                    continue;
                }

                MovementGroupSpec seed = new(
                    EventId: Pos.Domain.Common.EventId.New(),
                    MovementType: InventoryMovementType.OpeningBalance,
                    ReferenceDocumentType: ReferenceDocumentType.None,
                    ReferenceDocumentId: null,
                    ReferenceNumber: string.Empty,
                    Legs:
                    [
                        new MovementLegSpec(
                            productId,
                            BatchId: null,
                            external.Id,
                            LocationKind.External,
                            InventoryState.External,
                            -quantity,
                            unitCost,
                            ProductTracksBatches: false),
                        new MovementLegSpec(
                            productId,
                            BatchId: null,
                            id,
                            kind,
                            InventoryState.Available,
                            quantity,
                            unitCost,
                            ProductTracksBatches: false),
                    ],
                    Actor: new LedgerActor(UserId.Empty, UserId.Empty, null, CorrelationId.Empty),
                    OccurredAtUtc: openedAt,
                    BusinessDate: businessDate,
                    Notes: "Development opening balance for the demo catalogue.");

                Result<PostedMovementGroup> posted = await ledger.PostAsync(seed, cancellationToken).ConfigureAwait(false);

                if (posted.IsFailure)
                {
                    throw new InvalidOperationException(posted.Error.Message);
                }

                // The ledger stages into this same context. Because an opening
                // balance is a pair of legs, the external supplier bucket for a
                // product is touched again on the very next call; the change
                // tracker must be flushed and cleared so that bucket is loaded
                // from persistence instead of being created a second time.
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                context.ChangeTracker.Clear();

                _stockGroupsCreated++;
            }
        }
    }

    /// <summary>Gives every product stocking thresholds at each store, scaled
    /// from its opening quantity, so the replenishment view has targets to
    /// measure against. Existing settings are left alone.</summary>
    private async Task SeedLocationSettingsAsync(CancellationToken cancellationToken)
    {
        List<LocationId> stores = await context.Locations
            .AsNoTracking()
            .Where(l => l.Code == StoreOneCode || l.Code == StoreTwoCode || l.Code == StoreThreeCode)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach ((string sku, _, _, _, _, _, _, _, _, decimal storeOnHand, _) in Catalogue)
        {
            Product product = await context.Products
                .Include(p => p.LocationSettings)
                .SingleAsync(p => p.Sku == Sku.FromTrustedSource(sku), cancellationToken)
                .ConfigureAwait(false);

            // A store's target is a comfortable shelf for the product; the
            // reorder point sits at forty percent of it.
            decimal target = Math.Max(storeOnHand, 40m);
            decimal reorder = Math.Round(target * 0.4m, MidpointRounding.AwayFromZero);
            decimal minimum = Math.Round(target * 0.15m, MidpointRounding.AwayFromZero);

            foreach (LocationId store in stores)
            {
                if (product.LocationSettings.Any(setting => setting.LocationId == store))
                {
                    continue;
                }

                Result set = product.SetLocationSetting(
                    store, isStocked: true, minimum, reorder, target, target * 1.5m, target - reorder);

                if (set.IsFailure)
                {
                    throw new InvalidOperationException(set.Error.Message);
                }

                // Settings are exposed as a defensive copy; track the new row
                // explicitly, as the price rows are.
                context.ProductLocationSettings.Add(product.LocationSettings.Single(setting => setting.LocationId == store));
            }
        }
    }

    /// <summary>Named customers for the demo: account holders the registers can
    /// attach to a sale.</summary>
    private async Task SeedCustomersAsync(CancellationToken cancellationToken)
    {
        (string Name, string? Phone, string? Email, string? Tin, string? Note)[] customers =
        [
            ("Maria Santos", "0917 555 0101", "maria.santos@example.com", null, "Regular, weekly groceries."),
            ("Jose Reyes", "0918 555 0102", null, null, null),
            ("Ana Cruz", "0919 555 0103", "ana.cruz@example.com", null, null),
            ("Carlo Mendoza", "0920 555 0104", null, null, "Prefers e-wallet."),
            ("Liza Garcia", "0921 555 0105", "liza.garcia@example.com", null, null),
            ("Ramon Villanueva", "0922 555 0106", null, null, null),
            ("Grace Tan", "0923 555 0107", "grace.tan@example.com", null, null),
            ("Paolo Bautista", "0924 555 0108", null, null, null),
            ("Sunshine Carinderia", "0925 555 0109", "orders@sunshinecarinderia.example.com", "123-456-789-000", "Buys in bulk for the eatery."),
            ("Barangay Hall San Roque", "02 8555 0110", null, "987-654-321-000", "Official receipts required."),
            ("Kristine Ramos", "0926 555 0111", null, null, null),
            ("Miguel Aquino", "0927 555 0112", "miguel.aquino@example.com", null, null),
        ];

        AppUser? owner = await users.FindByNameAsync("owner").ConfigureAwait(false);
        if (owner is null)
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow;

        foreach ((string name, string? phone, string? email, string? tin, string? note) in customers)
        {
            bool exists = await context.Customers
                .AnyAsync(c => c.DisplayName == name, cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                continue;
            }

            Result<Pos.Domain.Sales.Customer> created = Pos.Domain.Sales.Customer.Create(
                CustomerId.New(), name, phone, email, tin, note, new UserId(owner.Id), now);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.Customers.Add(created.Value);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string MainLocationCode = "MAIN";
    private const string StoreOneCode = "STORE01";
    private const string StoreTwoCode = "STORE02";
    private const string StoreThreeCode = "STORE03";

    private async Task<Dictionary<string, UnitOfMeasureId>> EnsureUnitsOfMeasureAsync(CancellationToken ct)
    {
        (string Code, string Name, UnitKind Kind, int Decimals)[] units =
        [
            ("PC", "Piece", UnitKind.Count, 0),
            ("BOX", "Box", UnitKind.Count, 0),
            ("KG", "Kilogram", UnitKind.Weight, 2),
            ("L", "Litre", UnitKind.Volume, 2),
        ];

        Dictionary<string, UnitOfMeasureId> byCode = [];

        foreach ((string code, string name, UnitKind kind, int decimals) in units)
        {
            UnitOfMeasure? existing = await context.UnitsOfMeasure
                .FirstOrDefaultAsync(u => u.Code == code, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                byCode[code] = existing.Id;
                continue;
            }

            Result<UnitOfMeasure> created = UnitOfMeasure.Create(code, name, kind, decimals);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.UnitsOfMeasure.Add(created.Value);
            byCode[code] = created.Value.Id;
        }

        return byCode;
    }

    private async Task<Dictionary<string, CategoryId>> EnsureCategoriesAsync(CancellationToken ct)
    {
        (string Code, string Name, int SortOrder)[] categories =
        [
            ("GROCERIES", "Groceries", 10),
            ("BEVERAGES", "Beverages", 20),
            ("DAIRY", "Dairy", 30),
            ("HOUSEHOLD", "Household", 40),
            ("SNACKS", "Snacks", 50),
            ("BAKERY", "Bakery", 60),
            ("PERSONAL", "Personal Care", 70),
            ("FROZEN", "Frozen", 80),
        ];

        Dictionary<string, CategoryId> byCode = [];

        foreach ((string code, string name, int sortOrder) in categories)
        {
            ProductCategory? existing = await context.Categories
                .FirstOrDefaultAsync(c => c.Code == code, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                byCode[code] = existing.Id;
                continue;
            }

            Result<ProductCategory> created = ProductCategory.Create(code, name, parentId: null, sortOrder);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.Categories.Add(created.Value);
            byCode[code] = created.Value.Id;
        }

        return byCode;
    }

    private async Task<Dictionary<string, BrandId>> EnsureBrandsAsync(CancellationToken ct)
    {
        string[] brands = ["Green Valley", "Sunrise", "Golden Harvest", "Kitchen Best", "Pure Living"];

        Dictionary<string, BrandId> byName = [];

        foreach (string brandName in brands)
        {
            Brand? existing = await context.Brands
                .FirstOrDefaultAsync(b => b.Name == brandName, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                byName[brandName] = existing.Id;
                continue;
            }

            Result<Brand> created = Brand.Create(brandName);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.Brands.Add(created.Value);
            byName[brandName] = created.Value.Id;
        }

        return byName;
    }

    private async Task<Dictionary<string, SupplierId>> EnsureSuppliersAsync(CancellationToken ct)
    {
        (string Code, string Name)[] suppliers =
        [
            ("SUP1", "Metro Distribution"),
            ("SUP2", "Fresh Produce Co"),
            ("SUP3", "Island Beverages Inc."),
            ("SUP4", "HomeCare Supply"),
            ("SUP5", "Northern Bakers"),
        ];

        Dictionary<string, SupplierId> byCode = [];

        foreach ((string code, string name) in suppliers)
        {
            Supplier? existing = await context.Suppliers
                .FirstOrDefaultAsync(s => s.Code == code, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                byCode[code] = existing.Id;
                continue;
            }

            Result<Supplier> created = Supplier.Create(code, name, taxId: null, paymentTermsDays: 30, leadTimeDays: 7);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.Suppliers.Add(created.Value);
            byCode[code] = created.Value.Id;
        }

        return byCode;
    }

    private async Task SeedAccountsAsync(CancellationToken cancellationToken)
    {
        (string UserName, string DisplayName, string Role, ApprovalTier Tier, string? LocationCode)[]
            accounts =
            [
                ("owner", "Development Owner", Roles.Owner, ApprovalTier.Unlimited, null),
                ("admin", "System Administrator", Roles.Administrator, ApprovalTier.Tier3, null),
                ("manager", "Store One Manager", Roles.StoreManager, ApprovalTier.Tier1, "STORE01"),
                ("cashier", "Store One Cashier", Roles.Cashier, ApprovalTier.None, "STORE01"),
                ("inventory", "Inventory Staff", Roles.InventoryStaff, ApprovalTier.None, "MAIN"),
                ("auditor", "Auditor", Roles.Auditor, ApprovalTier.None, null),
            ];

        DateTimeOffset now = clock.UtcNow;

        foreach ((string userName, string displayName, string role, ApprovalTier tier, string? locationCode) in accounts)
        {
            AppUser? existing = await users.FindByNameAsync(userName).ConfigureAwait(false);

            if (existing is not null)
            {
                // Keep every development account on the shared password: when the
                // constant changes, existing accounts are rotated to it so the
                // credential set stays uniform and the demo never splits by age.
                if (!await users.CheckPasswordAsync(existing, DevelopmentPassword).ConfigureAwait(false))
                {
                    // The identity setup is AddIdentityCore-based and registers no
                    // token providers, so a token-based reset is not available.
                    // Removing and re-adding the password achieves the same result
                    // without one, and rotates the security stamp in the process.
                    IdentityResult removed = await users.RemovePasswordAsync(existing).ConfigureAwait(false);

                    if (!removed.Succeeded)
                    {
                        throw new InvalidOperationException(
                            FormattableString.Invariant(
                                $"Could not clear the password for {userName}: {string.Join("; ", removed.Errors.Select(e => e.Description))}"));
                    }

                    IdentityResult added = await users
                        .AddPasswordAsync(existing, DevelopmentPassword)
                        .ConfigureAwait(false);

                    if (!added.Succeeded)
                    {
                        throw new InvalidOperationException(
                            FormattableString.Invariant(
                                $"Could not rotate the password for {userName}: {string.Join("; ", added.Errors.Select(e => e.Description))}"));
                    }

                    logger.LogWarning("Rotated the password for development account {UserName}.", userName);
                }

                continue;
            }

            AppUser account = new()
            {
                Id = Guid.CreateVersion7(),
                UserName = userName,
                Email = null,
                EmailConfirmed = true,
                DisplayName = displayName,
                ApprovalTier = tier,
                IsActive = true,
                CreatedAtUtc = now,
                CreatedByUserId = Guid.Empty,
            };

            IdentityResult created = await users.CreateAsync(account, DevelopmentPassword).ConfigureAwait(false);

            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Could not create development account {userName}: {string.Join("; ", created.Errors.Select(e => e.Description))}"));
            }

            IdentityResult assigned = await users.AddToRoleAsync(account, role).ConfigureAwait(false);

            if (!assigned.Succeeded)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Could not assign role {role} to {userName}: {string.Join("; ", assigned.Errors.Select(e => e.Description))}"));
            }

            if (locationCode is not null)
            {
                LocationId? locationId = await context.Locations
                    .AsNoTracking()
                    .Where(l => l.Code == locationCode)
                    .Select(l => (LocationId?)l.Id)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (locationId is null)
                {
                    throw new InvalidOperationException(
                        FormattableString.Invariant($"Location {locationCode} is missing for development account {userName}."));
                }

                context.UserLocations.Add(UserLocationAssignment.Create(
                    new UserId(account.Id),
                    locationId.Value,
                    isPrimary: true,
                    now,
                    assignedByUserId: UserId.Empty));
            }

            _accountsCreated++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Retire the old per-store accounts so databases seeded before the
        // simplification present the same short login list. Accounts are
        // deactivated rather than deleted: historical sales, receipts and audit
        // rows reference them, and CanAuthenticate==false already stops every
        // sign-in path. Only seeder-created accounts (CreatedByUserId == Empty)
        // are touched; a real user that happens to share a username is left alone.
        foreach (string userName in ObsoleteDevelopmentAccounts)
        {
            AppUser? stale = await users.FindByNameAsync(userName).ConfigureAwait(false);

            if (stale is null || stale.CreatedByUserId != Guid.Empty || !stale.CanAuthenticate)
            {
                continue;
            }

            stale.IsActive = false;
            stale.DisabledAtUtc = now;
            stale.DisabledReason = "Retired by the simplified development credential set.";

            IdentityResult updated = await users.UpdateAsync(stale).ConfigureAwait(false);

            if (!updated.Succeeded)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Could not retire obsolete development account {userName}: {string.Join("; ", updated.Errors.Select(e => e.Description))}"));
            }

            logger.LogWarning("Retired obsolete development account {UserName}.", userName);
        }

        if (_accountsCreated > 0)
        {
            logger.LogWarning(
                "Created {Count} development account(s). The shared development password is '{Password}'. " +
                "Seeding:EnableDevelopmentAccounts must stay off everywhere else.",
                _accountsCreated,
                DevelopmentPassword);
        }
    }

    private Task<bool> LocationExistsAsync(string code, CancellationToken cancellationToken)
        => context.Locations.AnyAsync(l => l.Code == code, cancellationToken);
}
