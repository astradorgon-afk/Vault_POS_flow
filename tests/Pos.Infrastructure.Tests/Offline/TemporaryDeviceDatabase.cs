using Microsoft.Data.Sqlite;
using Pos.Application.Common.Abstractions;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>A migrated, encrypted device database in its own temporary directory.</summary>
internal sealed class TemporaryDeviceDatabase : IAsyncDisposable
{
    public const string EncryptionKey = "test-only-32-byte-device-key-value";

    public static readonly DateTimeOffset Now = new(2026, 9, 17, 2, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vaultflow-device-tests", Guid.NewGuid().ToString("N"));

    private TemporaryDeviceDatabase()
    {
        Initializer = new DeviceDatabaseInitializer(
            new DeviceDatabaseOptions(DatabasePath), new FixedKeyProvider(EncryptionKey));
        Applier = new ChangeFeedApplier(Initializer, new FixedClock(Now));
    }

    public string DatabasePath => Path.Combine(_directory, "device.db");

    public DeviceDatabaseInitializer Initializer { get; }

    public ChangeFeedApplier Applier { get; }

    /// <summary>A keyed connection with no device context behind it, as raw tooling would open.</summary>
    public string EncryptedConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Password = EncryptionKey,
        ForeignKeys = true,
        Pooling = false,
    }.ToString();

    public static async Task<TemporaryDeviceDatabase> CreateAsync()
    {
        TemporaryDeviceDatabase database = new();
        await database.Initializer.InitializeAsync(CancellationToken.None);
        return database;
    }

    public Task<PosDeviceDbContext> OpenContextAsync()
        => Initializer.CreateDbContextAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FixedKeyProvider(string key) : IDeviceDatabaseKeyProvider
    {
        public ValueTask<string> GetDatabaseKeyAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(key);
    }

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
