using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Pos.Infrastructure.Offline;

/// <summary>Supplies the device database key without exposing platform secure-storage APIs to infrastructure.</summary>
public interface IDeviceDatabaseKeyProvider
{
    /// <summary>Gets or creates the device-local key: exactly 256 random bits.</summary>
    /// <remarks>The caller clears the returned array once the key has been applied.</remarks>
    ValueTask<byte[]> GetDatabaseKeyAsync(CancellationToken cancellationToken);
}

/// <summary>Settings for the encrypted device database file.</summary>
/// <param name="DatabasePath">Absolute path to the SQLite database.</param>
public sealed record DeviceDatabaseOptions(string DatabasePath);

/// <summary>Creates, verifies and migrates the encrypted device database.</summary>
/// <remarks>
/// The key is 256 random bits, so it is given to SQLCipher as a raw key and no
/// passphrase derivation runs. Keyed as a passphrase, every connection open
/// repeated PBKDF2 and cost 650–800 ms, and connections are not pooled, so each
/// cache read paid it; as a raw key an open costs about 2 ms. The key is read
/// from the platform secure store once per process, and a failed read is retried
/// on the next call rather than remembered.
/// </remarks>
public sealed class DeviceDatabaseInitializer(
    DeviceDatabaseOptions options,
    IDeviceDatabaseKeyProvider keyProvider)
{
    /// <summary>The required key length in bytes.</summary>
    public const int KeyLengthBytes = 32;

    private readonly Lock _gate = new();
    private Task<KeyedStore>? _store;

    /// <summary>Opens the encrypted store and applies every pending device migration.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        KeyedStore store = await GetStoreAsync(cancellationToken).ConfigureAwait(false);

        string? directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await VerifyCipherAsync(store.ConnectionString, cancellationToken).ConfigureAwait(false);

        await using PosDeviceDbContext context = new(store.ContextOptions);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a context for the already-initialized encrypted store.</summary>
    public async Task<PosDeviceDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        KeyedStore store = await GetStoreAsync(cancellationToken).ConfigureAwait(false);
        return new PosDeviceDbContext(store.ContextOptions);
    }

    private Task<KeyedStore> GetStoreAsync(CancellationToken cancellationToken)
    {
        Task<KeyedStore> store;
        lock (_gate)
        {
            if (_store is null || _store.IsFaulted || _store.IsCanceled)
            {
                // Built without the caller's token, so one caller cancelling
                // does not fail the shared key read for everyone else.
                _store = KeyStoreAsync();
            }

            store = _store;
        }

        return store.WaitAsync(cancellationToken);
    }

    private async Task<KeyedStore> KeyStoreAsync()
    {
        if (!Path.IsPathFullyQualified(options.DatabasePath))
        {
            throw new InvalidOperationException("The device database path must be absolute.");
        }

        byte[] key = await keyProvider.GetDatabaseKeyAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (key is not { Length: KeyLengthBytes })
            {
                throw new InvalidOperationException("The device database key must be exactly 256 bits.");
            }

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                Password = RawKey(key),
                ForeignKeys = true,
                DefaultTimeout = 5,
            }.ToString();

            DbContextOptions<PosDeviceDbContext> contextOptions = new DbContextOptionsBuilder<PosDeviceDbContext>()
                .UseSqlite(connectionString, sqlite =>
                {
                    sqlite.MigrationsAssembly(typeof(PosDeviceDbContext).Assembly.FullName);
                    sqlite.MigrationsHistoryTable("__migrations_history");
                })
                .Options;

            return new KeyedStore(connectionString, contextOptions);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>SQLCipher's raw-key literal, which bypasses passphrase derivation.</summary>
    internal static string RawKey(ReadOnlySpan<byte> key) => "x'" + Convert.ToHexString(key) + "'";

    private static async Task VerifyCipherAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand cipher = connection.CreateCommand();
        cipher.CommandText = "PRAGMA cipher_version;";
        object? version = await cipher.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (version is not string { Length: > 0 })
        {
            throw new InvalidOperationException("The SQLite provider does not support encryption.");
        }

        // Only settings that outlive this connection belong here: memory security
        // is process-wide and the journal mode is stored in the file. Per-connection
        // settings stay at their defaults, so synchronous remains FULL and a
        // committed sale survives power loss; busy waits are bounded by the
        // command timeout.
        await using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA cipher_memory_security = ON; PRAGMA journal_mode = WAL;";
        await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The keyed connection string and options. A class rather than a record, so
    /// no generated <c>ToString</c> can print the key.
    /// </summary>
    private sealed class KeyedStore(string connectionString, DbContextOptions<PosDeviceDbContext> contextOptions)
    {
        public string ConnectionString { get; } = connectionString;

        public DbContextOptions<PosDeviceDbContext> ContextOptions { get; } = contextOptions;
    }
}
