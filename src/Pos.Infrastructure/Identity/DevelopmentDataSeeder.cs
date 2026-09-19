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
        await SeedStockAsync(cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (_seeding.Value.EnableDevelopmentAccounts)
        {
            await SeedAccountsAsync(cancellationToken).ConfigureAwait(false);
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

        (string Sku, string Name, string Category, string Brand, string Supplier, string Unit, string Barcode, decimal Price)[]
            catalogue =
            [
                ("RICE-01", "Premium Rice 5kg", "GROCERIES", "Green Valley", "SUP1", "KG", "4800000000017", 285m),
                ("COFFEE-200", "Ground Coffee 200g", "GROCERIES", "Sunrise", "SUP1", "KG", "4800000000024", 165m),
                ("SUGAR-1K", "Refined Sugar 1kg", "GROCERIES", "Green Valley", "SUP2", "KG", "4800000000031", 78m),
                ("OIL-1L", "Cooking Oil 1L", "GROCERIES", "Sunrise", "SUP2", "L", "4800000000048", 125m),
                ("MILK-370", "Evaporated Milk 370ml", "DAIRY", "Green Valley", "SUP1", "L", "4800000000055", 42m),
                ("EGGS-DZ", "Large Eggs (Dozen)", "DAIRY", "Green Valley", "SUP2", "PC", "4800000000062", 110m),
                ("WATER-500", "Mineral Water 500ml", "BEVERAGES", "Sunrise", "SUP1", "L", "4800000000079", 12m),
                ("SODA-1L", "Premium Soda 1L", "BEVERAGES", "Sunrise", "SUP2", "L", "4800000000086", 58m),
                ("DETER-400", "Laundry Powder 400g", "HOUSEHOLD", "Green Valley", "SUP1", "PC", "4800000000093", 135m),
            ];

        DateTimeOffset now = clock.UtcNow;

        foreach ((string sku, string name, string category, string brand, string supplier, string unit, string barcode, decimal price) in
                 catalogue)
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
                    primarySupplierId: supplierByCode[supplier]);

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

            bool hasCurrentOrFutureBusinessPrice = product.Prices.Any(p =>
                p.LocationId is null && (p.EffectiveToUtc is null || p.EffectiveToUtc > now));

            if (!hasCurrentOrFutureBusinessPrice)
            {
                Result<ProductPriceId> priceScheduled = product.SchedulePrice(
                    locationId: null,
                    amount: price,
                    effectiveFromUtc: now,
                    effectiveToUtc: null,
                    createdByUserId: UserId.Empty,
                    reason: "Development catalogue price.",
                    nowUtc: now);

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

    /// <summary>Stock levels for the demo: what each store holds and what the
    /// Main Warehouse holds, with the acquisition cost used to value the
    /// opening balance.</summary>
    private static readonly (string Sku, decimal StoreOnHand, decimal WarehouseOnHand, decimal UnitCost)[] Stock =
    [
        ("RICE-01", 25m, 300m, 240m),
        ("COFFEE-200", 20m, 220m, 132m),
        ("SUGAR-1K", 40m, 320m, 63m),
        ("OIL-1L", 30m, 280m, 99m),
        ("MILK-370", 60m, 500m, 33m),
        ("EGGS-DZ", 15m, 120m, 86m),
        ("WATER-500", 120m, 800m, 9m),
        ("SODA-1L", 48m, 240m, 44m),
        ("DETER-400", 36m, 200m, 106m),
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

        DateTimeOffset now = clock.UtcNow;
        DateOnly businessDate = clock.BusinessDateFor("Asia/Manila");

        foreach ((string sku, decimal storeOnHand, decimal warehouseOnHand, decimal unitCost) in Stock)
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
                    OccurredAtUtc: now,
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
        string[] brands = ["Green Valley", "Sunrise"];

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
        (string Code, string Name)[] suppliers = [("SUP1", "Metro Distribution"), ("SUP2", "Fresh Produce Co")];

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
                ("main.manager", "Main Warehouse Manager", Roles.MainInventoryManager, ApprovalTier.Tier2, null),
                ("s1.manager", "Store One Manager", Roles.StoreManager, ApprovalTier.Tier1, "STORE01"),
                ("s2.manager", "Store Two Manager", Roles.StoreManager, ApprovalTier.Tier1, "STORE02"),
                ("s3.manager", "Store Three Manager", Roles.StoreManager, ApprovalTier.Tier1, "STORE03"),
                ("inv.staff", "Inventory Staff", Roles.InventoryStaff, ApprovalTier.None, "MAIN"),
                ("s1.cashier", "Cashier One", Roles.Cashier, ApprovalTier.None, "STORE01"),
                ("s2.cashier", "Cashier Two", Roles.Cashier, ApprovalTier.None, "STORE02"),
                ("s3.cashier", "Cashier Three", Roles.Cashier, ApprovalTier.None, "STORE03"),
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
