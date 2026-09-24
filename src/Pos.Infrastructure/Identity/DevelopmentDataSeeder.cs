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
        await SeedBranchSettingsAsync(cancellationToken).ConfigureAwait(false);

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
        foreach (DevelopmentLocation location in DevelopmentCatalogue.Locations)
        {
            if (await LocationExistsAsync(location.Code, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            Result<Location> created = Location.Create(
                Organization.DefaultId,
                location.Code,
                location.Name,
                location.SizeFactor > 0m ? LocationKind.Store : LocationKind.MainWarehouse,
                "Asia/Manila");

            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            context.Locations.Add(created.Value);
            _locationsCreated++;
        }
    }

    /// <summary>Gives each branch the receipt header and footer a real store
    /// prints: trading name, branch, address and VAT registration, and the
    /// return policy. A branch whose receipt text someone has already set is
    /// left alone.</summary>
    private async Task SeedBranchSettingsAsync(CancellationToken cancellationToken)
    {
        foreach (DevelopmentLocation branch in DevelopmentCatalogue.Locations.Where(l => l.SizeFactor > 0m))
        {
            // The context reads untracked by default; this one is written back.
            Location? location = await context.Locations
                .AsTracking()
                .FirstOrDefaultAsync(l => l.Code == branch.Code, cancellationToken)
                .ConfigureAwait(false);

            if (location is null
                || location.Settings.ReceiptHeader.Length > 0
                || location.Settings.ReceiptFooter.Length > 0)
            {
                continue;
            }

            Result updated = location.UpdateSettings(location.Settings with
            {
                ReceiptHeader = $"{DevelopmentCatalogue.TradingName} {branch.Name}\n{branch.Address}\n{DevelopmentCatalogue.VatRegistration}-{branch.BranchCode}0",
                ReceiptFooter = "Salamat po! Keep this receipt for returns within 7 days.\nThis serves as your official receipt.",
            });

            if (updated.IsFailure)
            {
                throw new InvalidOperationException(updated.Error.Message);
            }
        }
    }

    private async Task SeedReferenceMasterDataAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, UnitOfMeasureId> uomByCode = await EnsureUnitsOfMeasureAsync(cancellationToken);
        Dictionary<string, CategoryId> categoryByCode = await EnsureCategoriesAsync(cancellationToken);
        Dictionary<string, BrandId> brandByName = await EnsureBrandsAsync(cancellationToken);
        Dictionary<string, SupplierId> supplierByCode = await EnsureSuppliersAsync(cancellationToken);

        // One read of what already exists, rather than a query per product.
        List<Sku> skus = [.. DevelopmentCatalogue.Products.Select(p => Sku.FromTrustedSource(p.Sku))];
        Dictionary<string, Product> existing = await context.Products
            .Include(p => p.Prices)
            .Where(p => skus.Contains(p.Sku))
            .ToDictionaryAsync(p => p.Sku.Value, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        if (existing.Count == 0 && await context.Products.AnyAsync(p => p.Sku == Sku.FromTrustedSource("RICE-01"), cancellationToken).ConfigureAwait(false))
        {
            logger.LogWarning(
                "This development database holds the earlier 43-product demo catalogue. The Suki Mart catalogue is " +
                "added alongside it; reset the database (docker volume rm vaultflow-dev-pgdata) for a clean demo business.");
        }

        DateTimeOffset historyStart = HistoryStartUtc;

        foreach (DevelopmentProduct item in DevelopmentCatalogue.Products)
        {
            if (existing.ContainsKey(item.Sku))
            {
                continue;
            }

            Result<Product> productResult = Product.Create(
                item.Sku,
                item.Name,
                categoryByCode[item.Category],
                uomByCode["PC"],
                UserId.Empty,
                brandId: brandByName[item.Brand],
                primarySupplierId: supplierByCode[item.Supplier],
                isVatExempt: item.VatExempt,
                defaultPurchaseCost: item.UnitCost);

            if (productResult.IsFailure)
            {
                throw new InvalidOperationException(productResult.Error.Message);
            }

            Product product = productResult.Value;

            Result barcodeAttached = product.AddBarcode(
                item.Barcode,
                uomByCode["PC"],
                packQuantity: 1m,
                isPrimary: true,
                UserId.Empty,
                requireChecksum: true);

            if (barcodeAttached.IsFailure)
            {
                throw new InvalidOperationException(barcodeAttached.Error.Message);
            }

            // The shelf price in effect from the start of the demo history, so
            // every backdated sale finds one. The seeder writes history as of
            // that instant, so the backdating guard is evaluated against it.
            SchedulePrice(product, item.Price, historyStart, "Opening shelf price.");

            // A few lines were repriced partway through the month, the way a
            // supplier price increase reaches the shelf. Sales before and after
            // the change carry the price in effect when they were rung up.
            if (DevelopmentCatalogue.Jitter(item.Sku, 17) < 0.06m)
            {
                decimal raised = item.Price < 100m
                    ? item.Price + Math.Max(1m, Math.Round(item.Price * 0.06m))
                    : Math.Ceiling(item.Price * 1.05m / 5m) * 5m;
                DateTimeOffset changedAt = historyStart.AddDays(14 + (int)(DevelopmentCatalogue.Jitter(item.Sku, 19) * 10m));
                SchedulePrice(product, raised, changedAt, "Supplier price increase.");
            }

            context.Products.Add(product);
            _productsCreated++;
        }
    }

    private void SchedulePrice(Product product, decimal amount, DateTimeOffset from, string reason)
    {
        Result<ProductPriceId> scheduled = product.SchedulePrice(
            locationId: null,
            amount: amount,
            effectiveFromUtc: from,
            effectiveToUtc: null,
            createdByUserId: UserId.Empty,
            reason: reason,
            nowUtc: from);

        if (scheduled.IsFailure)
        {
            throw new InvalidOperationException(scheduled.Error.Message);
        }

        _pricesCreated++;
    }

    /// <summary>How far back the demo history reaches. Prices and opening
    /// balances start here so the activity <c>tools/Pos.DemoData</c> posts over
    /// the last month (sales, returns, voids) always finds a price in effect and
    /// stock on hand.</summary>
    private const int HistoryDays = 35;

    private DateTimeOffset HistoryStartUtc
    {
        get
        {
            DateTimeOffset today = clock.UtcNow;
            return new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-HistoryDays);
        }
    }

    /// <summary>Posts each product's opening stock at the distribution centre
    /// and every branch as one ledger event, sized so a month of demo trading
    /// ends with a realistic spread of stock.</summary>
    private async Task SeedStockAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, (LocationId Id, LocationKind Kind)> locations = await context.Locations
            .AsNoTracking()
            .Select(l => new { l.Code, l.Id, l.Kind })
            .ToDictionaryAsync(l => l.Code, l => (l.Id, l.Kind), cancellationToken)
            .ConfigureAwait(false);

        if (!locations.TryGetValue(SystemLocationCodes.ExternalSupplier, out (LocationId Id, LocationKind Kind) external))
        {
            throw new InvalidOperationException("The external supplier location is missing for stock seeding.");
        }

        List<Sku> skus = [.. DevelopmentCatalogue.Products.Select(p => Sku.FromTrustedSource(p.Sku))];
        Dictionary<string, ProductId> productIds = await context.Products
            .AsNoTracking()
            .Where(p => skus.Contains(p.Sku))
            .ToDictionaryAsync(p => p.Sku.Value, p => p.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        HashSet<ProductId> alreadyOpened = [.. await context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.MovementType == InventoryMovementType.OpeningBalance && m.QuantityDelta > 0m)
            .Select(m => m.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)];

        // Opening balances date from the start of the demo history, so the
        // backdated sales the demo tool posts draw on stock already present.
        DateTimeOffset openedAt = HistoryStartUtc;
        DateOnly businessDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(openedAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila")).DateTime);

        foreach (DevelopmentProduct item in DevelopmentCatalogue.Products)
        {
            ProductId productId = productIds[item.Sku];
            if (alreadyOpened.Contains(productId))
            {
                continue;
            }

            List<MovementLegSpec> legs = [];
            foreach (DevelopmentLocation location in DevelopmentCatalogue.Locations)
            {
                decimal quantity = location.SizeFactor > 0m
                    ? DevelopmentCatalogue.StockingAt(item, location).Opening
                    : DevelopmentCatalogue.WarehouseOpening(item);
                if (quantity <= 0m)
                {
                    continue;
                }

                (LocationId id, LocationKind kind) = locations[location.Code];
                legs.Add(new MovementLegSpec(
                    productId, BatchId: null, id, kind, InventoryState.Available, quantity, item.UnitCost, ProductTracksBatches: false));
            }

            // Stock arrives from the outside world, so the supplier side of the
            // event is the negative of everything placed.
            legs.Insert(0, new MovementLegSpec(
                productId,
                BatchId: null,
                external.Id,
                LocationKind.External,
                InventoryState.External,
                -legs.Sum(l => l.QuantityDelta),
                item.UnitCost,
                ProductTracksBatches: false));

            MovementGroupSpec seed = new(
                EventId: Pos.Domain.Common.EventId.New(),
                MovementType: InventoryMovementType.OpeningBalance,
                ReferenceDocumentType: ReferenceDocumentType.None,
                ReferenceDocumentId: null,
                ReferenceNumber: string.Empty,
                Legs: legs,
                Actor: new LedgerActor(UserId.Empty, UserId.Empty, null, CorrelationId.Empty),
                OccurredAtUtc: openedAt,
                BusinessDate: businessDate,
                Notes: "Opening balance for the Suki Mart demo catalogue.");

            Result<PostedMovementGroup> posted = await ledger.PostAsync(seed, cancellationToken).ConfigureAwait(false);

            if (posted.IsFailure)
            {
                throw new InvalidOperationException(posted.Error.Message);
            }

            // The ledger stages into this same context. The external supplier
            // bucket for the next product is new, but the change tracker must
            // still be flushed so each event commits its projection before the
            // next one reads balances.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();

            _stockGroupsCreated++;
        }
    }

    /// <summary>Gives every product its stocking thresholds at each branch,
    /// from the rate it sells there, so the replenishment and stock views have
    /// real targets to measure against. Existing settings are left alone.</summary>
    private async Task SeedLocationSettingsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, LocationId> stores = await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind == LocationKind.Store)
            .ToDictionaryAsync(l => l.Code, l => l.Id, cancellationToken)
            .ConfigureAwait(false);

        List<Sku> skus = [.. DevelopmentCatalogue.Products.Select(p => Sku.FromTrustedSource(p.Sku))];
        Dictionary<string, Product> products = await context.Products
            .Include(p => p.LocationSettings)
            .Where(p => skus.Contains(p.Sku))
            .ToDictionaryAsync(p => p.Sku.Value, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        foreach (DevelopmentProduct item in DevelopmentCatalogue.Products)
        {
            Product product = products[item.Sku];

            foreach (DevelopmentLocation branch in DevelopmentCatalogue.Locations.Where(l => l.SizeFactor > 0m))
            {
                if (!stores.TryGetValue(branch.Code, out LocationId store)
                    || product.LocationSettings.Any(setting => setting.LocationId == store))
                {
                    continue;
                }

                DevelopmentStocking stocking = DevelopmentCatalogue.StockingAt(item, branch);
                Result set = product.SetLocationSetting(
                    store,
                    isStocked: true,
                    stocking.Minimum,
                    stocking.ReorderPoint,
                    stocking.Target,
                    stocking.Maximum,
                    stocking.Replenishment);

                if (set.IsFailure)
                {
                    throw new InvalidOperationException(set.Error.Message);
                }

                // Settings are exposed as a defensive copy; track the new row
                // explicitly.
                context.ProductLocationSettings.Add(product.LocationSettings.Single(setting => setting.LocationId == store));
            }
        }
    }

    /// <summary>Named customers: regulars with a loyalty account, and the
    /// eateries, offices and barangay halls that buy on an account.</summary>
    private async Task SeedCustomersAsync(CancellationToken cancellationToken)
    {
        AppUser? owner = await users.FindByNameAsync("owner").ConfigureAwait(false);
        if (owner is null)
        {
            return;
        }

        HashSet<string> present = [.. await context.Customers
            .AsNoTracking()
            .Select(c => c.DisplayName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)];

        DateTimeOffset now = clock.UtcNow;

        foreach ((string name, string? phone, string? email, string? tin, string? note) in DevelopmentCustomers.All)
        {
            if (present.Contains(name))
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
        Dictionary<string, CategoryId> byCode = [];

        foreach ((string code, string name, int sortOrder, _, _) in DevelopmentCatalogue.Categories)
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
        Dictionary<string, BrandId> byName = [];

        foreach (string brandName in DevelopmentCatalogue.Brands)
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
        Dictionary<string, SupplierId> byCode = [];

        foreach ((string code, string name, string taxId, int terms, int leadTime) in DevelopmentCatalogue.Suppliers)
        {
            Supplier? existing = await context.Suppliers
                .FirstOrDefaultAsync(s => s.Code == code, ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                byCode[code] = existing.Id;
                continue;
            }

            Result<Supplier> created = Supplier.Create(code, name, taxId, paymentTermsDays: terms, leadTimeDays: leadTime);

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
                ("owner", "Ramon Dela Cruz", Roles.Owner, ApprovalTier.Unlimited, null),
                ("admin", "Patricia Lim", Roles.Administrator, ApprovalTier.Tier3, null),
                ("warehouse", "Ernesto Villanueva", Roles.MainInventoryManager, ApprovalTier.Tier2, "MAIN"),
                ("inventory", "Jonathan Cruz", Roles.InventoryStaff, ApprovalTier.None, "MAIN"),
                ("manager", "Carmela Reyes", Roles.StoreManager, ApprovalTier.Tier1, "STORE01"),
                ("cashier", "Joy Mendoza", Roles.Cashier, ApprovalTier.None, "STORE01"),
                ("cashier1b", "Mark Anthony Santos", Roles.Cashier, ApprovalTier.None, "STORE01"),
                ("manager2", "Dennis Aquino", Roles.StoreManager, ApprovalTier.Tier1, "STORE02"),
                ("cashier2", "Kristine Bautista", Roles.Cashier, ApprovalTier.None, "STORE02"),
                ("cashier2b", "Rowena Garcia", Roles.Cashier, ApprovalTier.None, "STORE02"),
                ("manager3", "Lourdes Navarro", Roles.StoreManager, ApprovalTier.Tier1, "STORE03"),
                ("cashier3", "Paolo Ramos", Roles.Cashier, ApprovalTier.None, "STORE03"),
                ("cashier3b", "Janine Torres", Roles.Cashier, ApprovalTier.None, "STORE03"),
                ("auditor", "Teresa Gonzales", Roles.Auditor, ApprovalTier.None, null),
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

                // An account an older seed created under the same name (for
                // example cashier2, once a second Store One cashier) is brought
                // to its current name and store, so each store's staff actually
                // work at that store. Accounts a person created are left alone.
                if (existing.CreatedByUserId == Guid.Empty)
                {
                    await ConvergeDevelopmentAccountAsync(existing, displayName, locationCode, now, cancellationToken)
                        .ConfigureAwait(false);
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

    /// <summary>Gives a seeder-created account its configured display name and,
    /// for store and warehouse staff, makes the configured location its only one.</summary>
    private async Task ConvergeDevelopmentAccountAsync(
        AppUser account, string displayName, string? locationCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (account.DisplayName != displayName)
        {
            account.DisplayName = displayName;
            IdentityResult renamed = await users.UpdateAsync(account).ConfigureAwait(false);
            if (!renamed.Succeeded)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Could not rename development account {account.UserName}: {string.Join("; ", renamed.Errors.Select(e => e.Description))}"));
            }
        }

        // A business-wide account (owner, administrator, auditor) has no store
        // to converge on.
        if (locationCode is null)
        {
            return;
        }

        LocationId locationId = await context.Locations
            .AsNoTracking()
            .Where(l => l.Code == locationCode)
            .Select(l => l.Id)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        UserId userId = new(account.Id);
        List<UserLocationAssignment> assignments = await context.UserLocations
            .Where(a => a.UserId == userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (assignments.Count == 1 && assignments[0].LocationId == locationId)
        {
            return;
        }

        context.UserLocations.RemoveRange(assignments);
        context.UserLocations.Add(UserLocationAssignment.Create(userId, locationId, isPrimary: true, now, assignedByUserId: UserId.Empty));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Moved development account {UserName} to {Location}.", account.UserName, locationCode);
    }

    private Task<bool> LocationExistsAsync(string code, CancellationToken cancellationToken)
        => context.Locations.AnyAsync(l => l.Code == code, cancellationToken);
}
