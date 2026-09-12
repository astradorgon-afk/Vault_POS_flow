using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Inventory;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Tests.Inventory;

/// <summary>
/// Verifies <see cref="LocationSettingsLedgerPolicyProvider"/> resolves the
/// negative-stock policy from the location row.
/// </summary>
public sealed class LocationSettingsLedgerPolicyProviderTests : IAsyncLifetime
{
    private readonly string _connectionString =
        FormattableString.Invariant($"Data Source=vaultflow-policy-{Guid.CreateVersion7():N};Mode=Memory;Cache=Shared");

    private SqliteConnection? _connection;
    private DbContextOptions<PosDbContext>? _options;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using PosDbContext context = new(_options);
        await context.Database.EnsureCreatedAsync();

        LocationId main = LocationId.New();
        LocationId store = LocationId.New();

        Result<Location> mainLocation = Location.Create(
            Organization.DefaultId,
            "MAIN",
            "Main Warehouse",
            LocationKind.MainWarehouse,
            "Asia/Manila");

        if (mainLocation.IsFailure)
        {
            throw new InvalidOperationException("Could not create test MAIN location.");
        }

        Result<Location> storeLocation = Location.Create(
            Organization.DefaultId,
            "STORE01",
            "Store One",
            LocationKind.Store,
            "Asia/Manila",
            new LocationSettings { NegativeStockPolicy = NegativeStockPolicy.AllowWithPermission });

        if (storeLocation.IsFailure)
        {
            throw new InvalidOperationException("Could not create test STORE01 location.");
        }

        context.Locations.Add(mainLocation.Value);
        context.Locations.Add(storeLocation.Value);
        await context.SaveChangesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task LocationWithExplicitSettings_ReturnsThoseSettings()
    {
        await using PosDbContext context = new(_options!);

        LocationSettingsLedgerPolicyProvider provider = new(context);

        Guid storeId = await context.Locations
            .Where(l => l.Code == "STORE01")
            .Select(l => l.Id.Value)
            .FirstAsync();

        NegativeStockPolicy policy = await provider.GetNegativeStockPolicyAsync(
            new LocationId(storeId), CancellationToken.None);

        policy.Should().Be(NegativeStockPolicy.AllowWithPermission);
    }

    [Fact]
    public async Task LocationWithDefaultSettings_ReturnsProhibit()
    {
        await using PosDbContext context = new(_options!);

        LocationSettingsLedgerPolicyProvider provider = new(context);

        Guid mainId = await context.Locations
            .Where(l => l.Code == "MAIN")
            .Select(l => l.Id.Value)
            .FirstAsync();

        NegativeStockPolicy policy = await provider.GetNegativeStockPolicyAsync(
            new LocationId(mainId), CancellationToken.None);

        policy.Should().Be(NegativeStockPolicy.Prohibit);
    }

    [Fact]
    public async Task UnknownLocation_ReturnsProhibit()
    {
        await using PosDbContext context = new(_options!);

        LocationSettingsLedgerPolicyProvider provider = new(context);

        NegativeStockPolicy policy = await provider.GetNegativeStockPolicyAsync(
            new LocationId(Guid.CreateVersion7()), CancellationToken.None);

        policy.Should().Be(NegativeStockPolicy.Prohibit);
    }
}