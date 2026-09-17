using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

public sealed class DeviceDatabaseInitializerTests : IAsyncLifetime
{
    private static readonly byte[] EncryptionKey = TemporaryDeviceDatabase.EncryptionKey;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vaultflow-device-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "device.db");

    [Fact]
    public async Task InitializeAsync_CreatesOnlyTheScopedDeviceSchema_AndStoresDecimalsAsText()
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(new CountingKeyProvider(EncryptionKey));
        await initializer.InitializeAsync(CancellationToken.None);

        await using (PosDeviceDbContext context = await initializer.CreateDbContextAsync(CancellationToken.None))
        using (context.ChangeFeedWrites.Open())
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

        await using SqliteConnection connection = new(RawKeyConnectionString(EncryptionKey));
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
            "sync_cursor",
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
        DeviceDatabaseInitializer initializer = CreateInitializer(new CountingKeyProvider(EncryptionKey));
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
    public async Task InitializeAsync_KeysTheFileWithTheRawKey_SoNoPassphraseDerivationRuns()
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(new CountingKeyProvider(EncryptionKey));
        await initializer.InitializeAsync(CancellationToken.None);

        await using (SqliteConnection raw = new(RawKeyConnectionString(EncryptionKey)))
        {
            await raw.OpenAsync(CancellationToken.None);
            await using SqliteCommand command = raw.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            ((long)(await command.ExecuteScalarAsync(CancellationToken.None))!).Should().BePositive();
        }

        // The same key bytes as a passphrase derive a different key through PBKDF2,
        // so the file refusing them proves derivation is not what keyed it. The
        // refusal can come as early as the open, which reads the header.
        Func<Task> read = async () =>
        {
            await using SqliteConnection passphrase = new(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                Password = Convert.ToHexString(EncryptionKey),
            }.ToString());
            await passphrase.OpenAsync(CancellationToken.None);
            await using SqliteCommand refused = passphrase.CreateCommand();
            refused.CommandText = "SELECT count(*) FROM sqlite_master;";
            await refused.ExecuteScalarAsync(CancellationToken.None);
        };

        (await read.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(26, "SQLITE_NOTADB");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(33)]
    public async Task InitializeAsync_RefusesAKeyThatIsNot256Bits_BeforeCreatingTheFile(int length)
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(new CountingKeyProvider(new byte[length]));

        Func<Task> initialize = () => initializer.InitializeAsync(CancellationToken.None);

        (await initialize.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*256 bits*");
        File.Exists(DatabasePath).Should().BeFalse();
    }

    [Fact]
    public async Task Initializer_ReadsTheKeyOnce_AndClearsTheArrayItReceived()
    {
        CountingKeyProvider keys = new(EncryptionKey);
        DeviceDatabaseInitializer initializer = CreateInitializer(keys);

        await initializer.InitializeAsync(CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            await using PosDeviceDbContext context = await initializer.CreateDbContextAsync(CancellationToken.None);
            (await context.Products.AnyAsync(CancellationToken.None)).Should().BeFalse();
        }

        keys.Calls.Should().Be(1);
        keys.Handed.Should().ContainSingle().Which.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Initializer_RetriesAKeyReadThatFailed_InsteadOfRememberingTheFailure()
    {
        CountingKeyProvider keys = new(EncryptionKey) { FailFirstCall = true };
        DeviceDatabaseInitializer initializer = CreateInitializer(keys);

        Func<Task> first = () => initializer.InitializeAsync(CancellationToken.None);
        await first.Should().ThrowAsync<IOException>();

        await initializer.InitializeAsync(CancellationToken.None);
        keys.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ContextConnections_InheritTheWalJournal_AndKeepFullSynchronousWrites()
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(new CountingKeyProvider(EncryptionKey));
        await initializer.InitializeAsync(CancellationToken.None);

        await using PosDeviceDbContext context = await initializer.CreateDbContextAsync(CancellationToken.None);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        await using SqliteCommand journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        (await journal.ExecuteScalarAsync(CancellationToken.None)).Should().Be("wal");

        await using SqliteCommand synchronous = connection.CreateCommand();
        synchronous.CommandText = "PRAGMA synchronous;";
        (await synchronous.ExecuteScalarAsync(CancellationToken.None)).Should().Be(2L, "FULL: a committed sale survives power loss");

        await using SqliteCommand memorySecurity = connection.CreateCommand();
        memorySecurity.CommandText = "PRAGMA cipher_memory_security;";
        (await memorySecurity.ExecuteScalarAsync(CancellationToken.None)).Should().Be("1");
    }

    [Fact]
    public async Task PermissionSnapshot_RejectsDuplicateGlobalGrant()
    {
        DeviceDatabaseInitializer initializer = CreateInitializer(new CountingKeyProvider(EncryptionKey));
        await initializer.InitializeAsync(CancellationToken.None);

        await using PosDeviceDbContext context =
            await initializer.CreateDbContextAsync(CancellationToken.None);
        using IDisposable writes = context.ChangeFeedWrites.Open();
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

    private DeviceDatabaseInitializer CreateInitializer(IDeviceDatabaseKeyProvider keys)
        => new(new DeviceDatabaseOptions(DatabasePath), keys);

    private string RawKeyConnectionString(byte[] key)
        => new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Password = DeviceDatabaseInitializer.RawKey(key),
            ForeignKeys = true,
            Pooling = false,
        }.ToString();

    /// <summary>Hands out copies of a key and remembers each array it handed out.</summary>
    private sealed class CountingKeyProvider(byte[] key) : IDeviceDatabaseKeyProvider
    {
        public bool FailFirstCall { get; init; }

        public int Calls { get; private set; }

        public List<byte[]> Handed { get; } = [];

        public ValueTask<byte[]> GetDatabaseKeyAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (FailFirstCall && Calls == 1)
            {
                throw new IOException("The secure store is temporarily unavailable.");
            }

            byte[] copy = key.ToArray();
            Handed.Add(copy);
            return ValueTask.FromResult(copy);
        }
    }
}
