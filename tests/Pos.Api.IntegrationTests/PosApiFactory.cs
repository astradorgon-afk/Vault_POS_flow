using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Hosts the real API against an in-memory SQLite database.
/// </summary>
/// <remarks>
/// <para>
/// The point is to exercise the genuine pipeline: real middleware, real token
/// validation, the real authorization policy provider and handler, and the real
/// domain factories on writes. A test that calls a service directly proves the
/// service works; only a test that goes through the pipeline proves the endpoint
/// is actually protected and stores the right rows.
/// </para>
/// <para>
/// SQLite keeps it Docker-free so the suite runs anywhere. Deliberately no
/// development seed runs here: each test builds the exact topology it needs.
/// </para>
/// </remarks>
public sealed class PosApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Environment variables are process-wide, so an instance that overrides a
    // key must restore the value it displaced rather than nulling it: it may
    // not own the key (a sibling factory set it first).
    private readonly Dictionary<string, string?> _restoreValues = [];

    private readonly IReadOnlyDictionary<string, string?> _environmentOverrides;

    private readonly string _connectionString =
        FormattableString.Invariant($"Data Source=vaultflow-integration-{Guid.CreateVersion7():N};Mode=Memory;Cache=Shared");

    private SqliteConnection? _connection;
    private string? _signingKeyPem;

    /// <summary>Gets the password used for every seeded test account.</summary>
    public const string TestPassword = "correct-horse-battery-staple";

    /// <summary>Creates a factory with the standard test environment.</summary>
    public PosApiFactory()
        : this(new Dictionary<string, string?>())
    {
    }

    /// <summary>
    /// Creates a factory with extra environment overrides applied on top of the
    /// standard test environment. Useful for a test that needs a different
    /// configuration (for example a maintenance switch) without mutating the
    /// environment of sibling factories.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> so that xUnit still resolves this type as a collection
    /// fixture: fixtures may only expose a single public constructor.
    /// </remarks>
    /// <param name="environmentOverrides">Extra environment variables, with the
    /// <c>__</c> double-underscore configuration separator.</param>
    internal PosApiFactory(IReadOnlyDictionary<string, string?> environmentOverrides)
    {
        _environmentOverrides = environmentOverrides;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        using RSA rsa = RSA.Create(2048);
        _signingKeyPem = rsa.ExportRSAPrivateKeyPem();

        // Environment variables rather than ConfigureAppConfiguration, because
        // the entry point reads builder.Configuration eagerly - it needs a
        // connection string to register the DbContext - and that happens before
        // the factory's configuration callbacks are applied.
        foreach ((string key, string value) in TestConfiguration())
        {
            if (_restoreValues.ContainsKey(key))
            {
                continue;
            }

            _restoreValues[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        // A shared-cache in-memory database, not ":memory:". The keep-alive
        // connection below is what stops it being discarded.
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        // The schema is created before the host starts, because the API seeds
        // permissions and roles during start-up and would otherwise find no
        // tables to seed into.
        DbContextOptions<PosDbContext> options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using PosDbContext context = new(options);
        await context.Database.EnsureCreatedAsync();
    }

    private IEnumerable<(string Key, string Value)> TestConfiguration()
    {
        yield return ("ASPNETCORE_ENVIRONMENT", "Testing");

        yield return ("Database__Provider", "Sqlite");
        yield return ("ConnectionStrings__Sqlite", _connectionString);

        yield return ("Jwt__Issuer", "https://tests.vaultflow.local");
        yield return ("Jwt__Audience", "vaultflow-api");
        yield return ("Jwt__SigningKeyPem", _signingKeyPem!);
        yield return ("Jwt__AccessTokenMinutes", "10");
        yield return ("Jwt__ClockSkewSeconds", "0");
        yield return ("Database__ApplyMigrationsOnStartup", "false");
        yield return ("Database__SeedDevelopmentData", "false");
        yield return ("BootstrapOwner__Enabled", "false");

        // Password hashing would otherwise dominate the runtime of every
        // authentication test.
        yield return ("Security__PasswordHashIterations", "100000");

        // Every test in this collection signs in from the same loopback address,
        // so the production limits would have them throttling each other.
        yield return ("RateLimits__LoginPermitLimit", "10000");
        yield return ("RateLimits__RefreshPermitLimit", "10000");
        yield return ("RateLimits__GlobalPermitLimit", "100000");

        // Instance overrides come last so they displace the shared defaults for
        // this factory's host only.
        foreach ((string key, string? value) in _environmentOverrides)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return (key, value);
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        // Environment variables are process-wide, so they are restored to the
        // values this instance displaced (usually null) rather than hard-cleared,
        // so a sibling factory's environment is never torn down.
        foreach ((string key, string? original) in _restoreValues)
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    /// <inheritdoc />
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Nothing is replaced. The host runs its own registrations against the
        // SQLite provider it was configured with, so these tests exercise the
        // production pipeline rather than a rearranged copy of it.
        builder?.UseEnvironment("Testing");
    }

    /// <summary>Creates a user with the given roles, locations and approval tier.</summary>
    /// <param name="userName">The username.</param>
    /// <param name="role">The role to assign.</param>
    /// <param name="locations">The locations to assign the user to.</param>
    /// <param name="tier">The approval tier.</param>
    /// <returns>The created user's identifier.</returns>
    public async Task<UserId> CreateUserAsync(
        string userName,
        string role,
        IReadOnlyList<LocationId>? locations = null,
        ApprovalTier tier = ApprovalTier.None)
    {
        using IServiceScope scope = Services.CreateScope();

        Microsoft.AspNetCore.Identity.UserManager<AppUser> users =
            scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();

        PosDbContext context = scope.ServiceProvider.GetRequiredService<PosDbContext>();

        AppUser user = new()
        {
            Id = Guid.CreateVersion7(),
            UserName = userName,
            Email = userName + "@tests.local",
            EmailConfirmed = true,
            DisplayName = userName,
            ApprovalTier = tier,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        Microsoft.AspNetCore.Identity.IdentityResult created =
            await users.CreateAsync(user, TestPassword);

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not create test user: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(user, role);

        UserId id = new(user.Id);

        foreach (LocationId location in locations ?? [])
        {
            context.UserLocations.Add(UserLocationAssignment.Create(
                id, location, isPrimary: true, DateTimeOffset.UtcNow, id));
        }

        await context.SaveChangesAsync();

        // Role and location changes move the authorization policy forward, which
        // is what evicts any cached permission set for this user.
        IPolicyVersionProvider policyVersion = scope.ServiceProvider.GetRequiredService<IPolicyVersionProvider>();
        await policyVersion.BumpAsync("test user created", CancellationToken.None);

        return id;
    }

    /// <summary>Creates a physical location directly in the database, or returns
    /// the one already bearing the code. The database is shared by every test in
    /// a class, so seeds are idempotent by code.</summary>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The display name.</param>
    /// <param name="kind">The kind; not external.</param>
    /// <returns>The location's identifier.</returns>
    public Task<LocationId> CreateLocationAsync(string code, string name, LocationKind kind = LocationKind.Store)
        => WithServiceAsync<LocationId>(async context =>
        {
            Location? existing = await context.Locations.FirstOrDefaultAsync(l => l.Code == code);

            if (existing is not null)
            {
                return existing.Id;
            }

            Result<Location> created = Location.Create(
                Organization.DefaultId, code, name, kind, "Asia/Manila");

            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "Could not create test location: " + string.Join("; ", created.Errors.Select(e => e.Code)));
            }

            context.Locations.Add(created.Value);
            await context.SaveChangesAsync();
            return created.Value.Id;
        });

    /// <summary>Creates a system external counterparty directly in the database,
    /// or returns the one already bearing the code.</summary>
    /// <param name="code">One of the well-known counterparty codes.</param>
    /// <returns>The location's identifier.</returns>
    public Task<LocationId> CreateExternalLocationAsync(string code)
        => WithServiceAsync<LocationId>(async context =>
        {
            Location? existing = await context.Locations.FirstOrDefaultAsync(l => l.Code == code);

            if (existing is not null)
            {
                return existing.Id;
            }

            Result<Location> created = Location.CreateSystemExternal(Organization.DefaultId, code, "Test " + code);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "Could not create test counterparty: " + string.Join("; ", created.Errors.Select(e => e.Code)));
            }

            context.Locations.Add(created.Value);
            await context.SaveChangesAsync();
            return created.Value.Id;
        });

    /// <summary>Creates a category directly in the database, or returns the one
    /// already bearing the code.</summary>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The display name.</param>
    /// <returns>The category's identifier.</returns>
    public Task<CategoryId> CreateCategoryAsync(string code, string name)
        => WithServiceAsync<CategoryId>(async context =>
        {
            ProductCategory? existing = await context.Categories.FirstOrDefaultAsync(c => c.Code == code);

            if (existing is not null)
            {
                return existing.Id;
            }

            Result<ProductCategory> created = ProductCategory.Create(code, name, parentId: null);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "Could not create test category: " + string.Join("; ", created.Errors.Select(e => e.Code)));
            }

            context.Categories.Add(created.Value);
            await context.SaveChangesAsync();
            return created.Value.Id;
        });

    /// <summary>Creates a unit of measure directly in the database, or returns
    /// the one already bearing the code.</summary>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The display name.</param>
    /// <param name="kind">The measurement kind.</param>
    /// <returns>The unit's identifier.</returns>
    public Task<UnitOfMeasureId> CreateUnitOfMeasureAsync(string code, string name, UnitKind kind = UnitKind.Count)
        => WithServiceAsync<UnitOfMeasureId>(async context =>
        {
            UnitOfMeasure? existing = await context.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Code == code);

            if (existing is not null)
            {
                return existing.Id;
            }

            Result<UnitOfMeasure> created = UnitOfMeasure.Create(code, name, kind);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "Could not create test unit of measure: " + string.Join("; ", created.Errors.Select(e => e.Code)));
            }

            context.UnitsOfMeasure.Add(created.Value);
            await context.SaveChangesAsync();
            return created.Value.Id;
        });

    /// <summary>
    /// Creates a product with its category, base unit and one primary barcode
    /// directly in the database.
    /// </summary>
    /// <param name="sku">The stock-keeping unit code.</param>
    /// <param name="name">The display name.</param>
    /// <param name="barcode">The primary barcode value.</param>
    /// <param name="tracksBatches">Whether the product is tracked by lot.</param>
    /// <param name="tracksExpiry">Whether the product carries an expiry date.</param>
    /// <param name="defaultPurchaseCost">Default purchase cost used when a
    /// movement does not carry a batch with a cost snapshot.</param>
    /// <returns>The created product's identifier.</returns>
    public Task<ProductId> CreateProductAsync(
        string sku, string name, string barcode, bool tracksBatches = false, bool tracksExpiry = false,
        decimal defaultPurchaseCost = 0m)
        => WithServiceAsync<ProductId>(async context =>
        {
            CategoryId categoryId = (await SeedCategoryAsync(context, "CATEGORY", "Category")).Id;
            UnitOfMeasureId unitId = (await SeedUnitAsync(context, "PC", "Piece")).Id;
            UserId systemUser = new(Guid.CreateVersion7());

            Result<Product> created = Product.Create(
                sku, name, categoryId, unitId, systemUser,
                tracksBatches: tracksBatches,
                tracksExpiry: tracksExpiry,
                shelfLifeDays: tracksExpiry ? 270 : null,
                defaultPurchaseCost: defaultPurchaseCost);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "Could not create test product: " + string.Join("; ", created.Errors.Select(e => e.Code)));
            }

            Result barcodeAttached = created.Value.AddBarcode(
                barcode, unitId, packQuantity: 1m, isPrimary: true, systemUser, requireChecksum: false);

            if (barcodeAttached.IsFailure)
            {
                throw new InvalidOperationException(
                    "Could not attach test barcode: " + string.Join("; ", barcodeAttached.Errors.Select(e => e.Code)));
            }

            context.Products.Add(created.Value);
            await context.SaveChangesAsync();
            return created.Value.Id;
        });

    /// <summary>Creates a supplier directly in the database, or returns the one
    /// already bearing the code.</summary>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The display name.</param>
    /// <returns>The supplier's identifier.</returns>
    public Task<SupplierId> CreateSupplierAsync(string code, string name)
        => WithServiceAsync<SupplierId>(async context =>
        {
            Supplier? existing = await context.Suppliers.FirstOrDefaultAsync(s => s.Code == code);

            if (existing is not null)
            {
                return existing.Id;
            }

            Result<Supplier> created = Supplier.Create(code, name, taxId: null, paymentTermsDays: 30, leadTimeDays: 7);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "Could not create test supplier: " + string.Join("; ", created.Errors.Select(e => e.Code)));
            }

            context.Suppliers.Add(created.Value);
            await context.SaveChangesAsync();
            return created.Value.Id;
        });

    private static async Task<ProductCategory> SeedCategoryAsync(PosDbContext context, string code, string name)
    {
        ProductCategory? existing = await context.Categories.FirstOrDefaultAsync(c => c.Code == code);

        if (existing is not null)
        {
            return existing;
        }

        Result<ProductCategory> created = ProductCategory.Create(code, name, parentId: null);

        if (created.IsFailure)
        {
            throw new InvalidOperationException(
                "Could not create test category: " + string.Join("; ", created.Errors.Select(e => e.Code)));
        }

        context.Categories.Add(created.Value);
        return created.Value;
    }

    private static async Task<UnitOfMeasure> SeedUnitAsync(PosDbContext context, string code, string name)
    {
        UnitOfMeasure? existing = await context.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Code == code);

        if (existing is not null)
        {
            return existing;
        }

        Result<UnitOfMeasure> created = UnitOfMeasure.Create(code, name, UnitKind.Count);

        if (created.IsFailure)
        {
            throw new InvalidOperationException(
                "Could not create test unit of measure: " + string.Join("; ", created.Errors.Select(e => e.Code)));
        }

        context.UnitsOfMeasure.Add(created.Value);
        return created.Value;
    }

    /// <summary>Runs an action against a scoped service.</summary>
    /// <typeparam name="TResult">The action's result type.</typeparam>
    /// <param name="action">What to do with the context.</param>
    /// <returns>A task that completes when the action does.</returns>
    public async Task<TResult> WithServiceAsync<TResult>(Func<PosDbContext, Task<TResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using IServiceScope scope = Services.CreateScope();
        PosDbContext context = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        return await action(context);
    }

    /// <summary>Runs an action against a scoped service.</summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    /// <param name="action">What to do with it.</param>
    /// <returns>A task that completes when the action does.</returns>
    public async Task WithServiceAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(action);

        using IServiceScope scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
}

/// <summary>Shares one API host across a test class.</summary>
[CollectionDefinition("api")]
public sealed class ApiFixtureDefinition : ICollectionFixture<PosApiFactory>;