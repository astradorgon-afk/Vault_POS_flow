using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Identity;

/// <summary>What the development seeder created or found already present.</summary>
/// <param name="LocationsCreated">Physical and external locations created.</param>
/// <param name="ProductsCreated">Products created.</param>
/// <param name="AccountsCreated">Staff accounts created, when account seeding is enabled.</param>
public sealed record DevelopmentDataSummary(
    int LocationsCreated,
    int ProductsCreated,
    int AccountsCreated)
{
    /// <summary>The summary when seeding was not requested.</summary>
    public static DevelopmentDataSummary None { get; } = new(0, 0, 0);
}

/// <summary>
/// Seeds the development database with a usable topology: the three system
/// counterparties, one Main Warehouse, three stores, reference master data, a
/// small product catalogue and — when explicitly enabled — staff accounts with a
/// known password.
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
/// <param name="database">Database configuration.</param>
/// <param name="seeding">Seeding configuration.</param>
/// <param name="clock">The authoritative clock.</param>
/// <param name="logger">Logger.</param>
public sealed class DevelopmentDataSeeder(
    PosDbContext context,
    UserManager<AppUser> users,
    IOptions<DatabaseOptions> database,
    IOptions<SeedingOptions> seeding,
    ISystemClock clock,
    ILogger<DevelopmentDataSeeder> logger)
{
    /// <summary>The password every development account shares. Development only,
    /// and documented so nobody mistakes it for a deployed credential.</summary>
    private const string DevelopmentPassword = "DevVaultFlow!2026";

    private readonly IOptions<DatabaseOptions> _database = database;
    private readonly IOptions<SeedingOptions> _seeding = seeding;

    private int _locationsCreated;
    private int _productsCreated;
    private int _accountsCreated;

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

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (_seeding.Value.EnableDevelopmentAccounts)
        {
            await SeedAccountsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Development seed complete: {Locations} locations, {Products} products, {Accounts} accounts.",
                _locationsCreated,
                _productsCreated,
                _accountsCreated);
        }

        return new DevelopmentDataSummary(_locationsCreated, _productsCreated, _accountsCreated);
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

        (string Sku, string Name, string Category, string Brand, string Supplier, string Unit, string Barcode)[]
            catalogue =
            [
                ("RICE-01", "Premium Rice 5kg", "GROCERIES", "Green Valley", "SUP1", "KG", "4800000000017"),
                ("COFFEE-200", "Ground Coffee 200g", "GROCERIES", "Sunrise", "SUP1", "KG", "4800000000024"),
                ("SUGAR-1K", "Refined Sugar 1kg", "GROCERIES", "Green Valley", "SUP2", "KG", "4800000000031"),
                ("OIL-1L", "Cooking Oil 1L", "GROCERIES", "Sunrise", "SUP2", "L", "4800000000048"),
                ("MILK-370", "Evaporated Milk 370ml", "DAIRY", "Green Valley", "SUP1", "L", "4800000000055"),
                ("EGGS-DZ", "Large Eggs (Dozen)", "DAIRY", "Green Valley", "SUP2", "PC", "4800000000062"),
            ];

        foreach ((string sku, string name, string category, string brand, string supplier, string unit, string barcode) in
                 catalogue)
        {
            if (await context.Products
                .AnyAsync(p => p.Sku == Sku.FromTrustedSource(sku), cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

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

            Product product = productResult.Value;

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
                ("main.mgr", "Main Inventory Manager", Roles.MainInventoryManager, ApprovalTier.Tier2, null),
                ("store1.mgr", "Store One Manager", Roles.StoreManager, ApprovalTier.Tier1, "STORE01"),
                ("store2.mgr", "Store Two Manager", Roles.StoreManager, ApprovalTier.Tier1, "STORE02"),
                ("store3.mgr", "Store Three Manager", Roles.StoreManager, ApprovalTier.Tier1, "STORE03"),
                ("inv.staff", "Inventory Staff", Roles.InventoryStaff, ApprovalTier.None, "MAIN"),
                ("cashier1", "Cashier One", Roles.Cashier, ApprovalTier.None, "STORE01"),
                ("cashier2", "Cashier Two", Roles.Cashier, ApprovalTier.None, "STORE01"),
                ("auditor", "Auditor", Roles.Auditor, ApprovalTier.None, null),
            ];

        DateTimeOffset now = clock.UtcNow;

        foreach ((string userName, string displayName, string role, ApprovalTier tier, string? locationCode) in accounts)
        {
            AppUser? existing = await users.FindByNameAsync(userName).ConfigureAwait(false);

            if (existing is not null)
            {
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