using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Security.Tests;

/// <summary>
/// Hosts the real API against an in-memory database.
/// </summary>
/// <remarks>
/// <para>
/// The point is to exercise the genuine pipeline: real middleware, real token
/// validation, the real authorization policy provider and handler. A test that
/// calls a service directly proves the service works; only a test that goes
/// through the pipeline proves the endpoint is actually protected.
/// </para>
/// <para>
/// SQLite keeps it Docker-free so the suite runs anywhere. The database-level
/// guarantees that only PostgreSQL can give — triggers, role grants — are proved
/// separately in <c>Pos.Infrastructure.Tests</c>.
/// </para>
/// </remarks>
public sealed class PosApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly List<string> _environmentKeys = [];

    private readonly string _connectionString =
        FormattableString.Invariant($"Data Source=vaultflow-tests-{Guid.CreateVersion7():N};Mode=Memory;Cache=Shared");

    private SqliteConnection? _connection;
    private string? _signingKeyPem;

    /// <summary>Gets the password used for every seeded test account.</summary>
    public const string TestPassword = "correct-horse-battery-staple";

    /// <summary>Gets the PIN used for every seeded cashier.</summary>
    public const string TestPin = "481516";

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        using RSA rsa = RSA.Create(2048);
        _signingKeyPem = rsa.ExportRSAPrivateKeyPem();

        // Environment variables rather than ConfigureAppConfiguration, because
        // the entry point reads builder.Configuration eagerly - it needs a
        // connection string to register the DbContext - and that happens before
        // the factory's configuration callbacks are applied. The builder reads
        // environment variables by default, so these are visible in time.
        foreach ((string key, string value) in TestConfiguration())
        {
            Environment.SetEnvironmentVariable(key, value);
            _environmentKeys.Add(key);
        }

        // A shared-cache in-memory database, not ":memory:". The private form
        // gives every connection its own empty database; the shared form lets the
        // application open its own connections and see the same data. The keep-
        // alive connection below is what stops it being discarded.
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
        yield return ("BootstrapOwner__Enabled", "false");

        // Owners and administrators sign in throughout the suite; two-factor
        // enforcement is covered by its own tests in Pos.Api.IntegrationTests.
        yield return ("Security__RequireTwoFactorForAdmins", "false");

        // Password hashing would otherwise dominate the runtime of every
        // authentication test. The production count is asserted separately,
        // against configuration, rather than paid for here.
        yield return ("Security__PasswordHashIterations", "100000");

        // Every test in this collection signs in from the same loopback address,
        // so the production limits would have them throttling each other.
        // Throttling itself is proved by ThrottlingTests, which sets its own.
        yield return ("RateLimits__LoginPermitLimit", "10000");
        yield return ("RateLimits__RefreshPermitLimit", "10000");
        yield return ("RateLimits__GlobalPermitLimit", "100000");
    }

    /// <inheritdoc />
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        // Environment variables are process-wide, so they are removed again
        // rather than left to leak into whatever runs next in this process.
        foreach (string key in _environmentKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

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
    /// <param name="employeeCode">An employee code, when the user signs in by PIN.</param>
    /// <returns>The created user's identifier.</returns>
    public async Task<UserId> CreateUserAsync(
        string userName,
        string role,
        IReadOnlyList<LocationId>? locations = null,
        ApprovalTier tier = ApprovalTier.None,
        string? employeeCode = null)
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
            EmployeeCode = employeeCode,
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

        if (employeeCode is not null)
        {
            user.PinHash = users.PasswordHasher.HashPassword(user, TestPin);
            await users.UpdateAsync(user);
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

    /// <summary>Registers and enrols a device at a location.</summary>
    /// <param name="shortCode">The document-number short code.</param>
    /// <param name="locationId">The location.</param>
    /// <param name="status">The status to leave the device in.</param>
    /// <returns>The enrolled device's identifier.</returns>
    public async Task<DeviceId> CreateDeviceAsync(
        string shortCode,
        LocationId locationId,
        DeviceStatus status = DeviceStatus.Active)
    {
        using IServiceScope scope = Services.CreateScope();
        PosDbContext context = scope.ServiceProvider.GetRequiredService<PosDbContext>();

        UserId systemUser = new(Guid.CreateVersion7());

        Result<Device> device = Device.Register(
            shortCode, "Test " + shortCode, locationId, DevicePlatform.Windows, DateTimeOffset.UtcNow, systemUser);

        if (device.IsFailure)
        {
            throw new InvalidOperationException(
                "Could not register test device: " + string.Join("; ", device.Errors.Select(e => e.Code)));
        }

        if (status != DeviceStatus.PendingEnrolment)
        {
            device.Value.CompleteEnrolment("test-thumbprint-" + shortCode, "1.0.0", "test", DateTimeOffset.UtcNow);
        }

        if (status == DeviceStatus.Suspended)
        {
            device.Value.Suspend("suspended by a test", DateTimeOffset.UtcNow, systemUser);
        }
        else if (status == DeviceStatus.Revoked)
        {
            device.Value.Revoke("revoked by a test", DateTimeOffset.UtcNow, systemUser);
        }

        context.Devices.Add(device.Value);
        await context.SaveChangesAsync();

        return device.Value.Id;
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
