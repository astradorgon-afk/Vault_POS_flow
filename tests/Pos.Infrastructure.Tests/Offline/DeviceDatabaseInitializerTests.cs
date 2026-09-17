using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

public sealed class DeviceDatabaseInitializerTests : IAsyncLifetime
{
    private const string EncryptionKey = "test-only-32-byte-device-key-value";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vaultflow-device-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "device.db");

    [Fact]
    public async Task InitializeAsync_CreatesOnlyTheScopedDeviceSchema_AndStoresDecimalsAsText()
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(EncryptionKey);
        await initializer.InitializeAsync(CancellationToken.None);

        await using (PosDeviceDbContext context = await initializer.CreateDbContextAsync(CancellationToken.None))
        {
            ProductId productId = ProductId.New();
            context.Products.Add(new DeviceCachedProduct(
                productId, "SKU-DEVICE-1", "Offline rice", true, false, false, 7,
                new DateTimeOffset(2026, 9, 17, 1, 30, 0, TimeSpan.Zero)));
            context.ProductPrices.Add(new DeviceCachedProductPrice(
                ProductPriceId.New(), productId, null, 12.3456m, "PHP",
                new DateTimeOffset(2026, 9, 17, 1, 0, 0, TimeSpan.Zero), null));
            await context.SaveChangesAsync(CancellationToken.None);
        }

        await using SqliteConnection connection = new(EncryptedConnectionString(EncryptionKey));
        await connection.OpenAsync(CancellationToken.None);

        List<string> tables = [];
        await using (SqliteCommand tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using SqliteDataReader reader = await tableCommand.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                tables.Add(reader.GetString(0));
            }
        }

        tables.Should().Contain(
        [
            "__migrations_history",
            "cache_location",
            "cache_product",
            "cache_product_barcode",
            "cache_product_price",
            "cache_user",
            "device_profile",
            "snapshot_permission",
        ]);
        tables.Should().NotContain(["AspNetUsers", "audit_log", "purchase_order"]);

        await using SqliteCommand amountCommand = connection.CreateCommand();
        amountCommand.CommandText = "SELECT amount, typeof(amount) FROM cache_product_price;";
        await using SqliteDataReader amount = await amountCommand.ExecuteReaderAsync(CancellationToken.None);
        (await amount.ReadAsync(CancellationToken.None)).Should().BeTrue();
        amount.GetString(0).Should().Be("12.3456");
        amount.GetString(1).Should().Be("text");
    }

    [Fact]
    public async Task InitializeAsync_ProducesAFileThatCannotBeReadWithoutItsKey()
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(EncryptionKey);
        await initializer.InitializeAsync(CancellationToken.None);
        SqliteConnection.ClearAllPools();

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master;";

        Func<Task> read = async () => await command.ExecuteScalarAsync(CancellationToken.None);
        await read.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task PermissionSnapshot_RejectsDuplicateGlobalGrant()
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(EncryptionKey);
        await initializer.InitializeAsync(CancellationToken.None);

        await using PosDeviceDbContext context =
            await initializer.CreateDbContextAsync(CancellationToken.None);
        UserId userId = UserId.New();
        DateTimeOffset issuedAt = new(2026, 9, 17, 1, 30, 0, TimeSpan.Zero);
        context.PermissionSnapshots.AddRange(
            new DevicePermissionSnapshot(
                Guid.NewGuid(), userId, "sale.create", null, 1, issuedAt, issuedAt.AddHours(8)),
            new DevicePermissionSnapshot(
                Guid.NewGuid(), userId, "sale.create", null, 2, issuedAt, issuedAt.AddHours(8)));

        Func<Task> save = async () => await context.SaveChangesAsync(CancellationToken.None);
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private DeviceDatabaseInitializer CreateInitializer(string key)
        => new(new DeviceDatabaseOptions(DatabasePath), new FixedKeyProvider(key));

    private string EncryptedConnectionString(string key)
        => new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Password = key,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();

    private sealed class FixedKeyProvider(string key) : IDeviceDatabaseKeyProvider
    {
        public ValueTask<string> GetDatabaseKeyAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(key);
    }
}
