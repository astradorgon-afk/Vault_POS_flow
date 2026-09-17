using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Pos.Infrastructure.Offline;

/// <summary>Supplies the device database key without exposing platform secure-storage APIs to infrastructure.</summary>
public interface IDeviceDatabaseKeyProvider
{
    /// <summary>Gets or creates the device-local encryption key.</summary>
    ValueTask<string> GetDatabaseKeyAsync(CancellationToken cancellationToken);
}

/// <summary>Settings for the encrypted device database file.</summary>
/// <param name="DatabasePath">Absolute path to the SQLite database.</param>
public sealed record DeviceDatabaseOptions(string DatabasePath);

/// <summary>Creates, verifies and migrates the encrypted device database.</summary>
public sealed class DeviceDatabaseInitializer(
    DeviceDatabaseOptions options,
    IDeviceDatabaseKeyProvider keyProvider)
{
    /// <summary>Opens the encrypted store and applies every pending device migration.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string connectionString = await BuildConnectionStringAsync(cancellationToken).ConfigureAwait(false);

        string? directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await VerifyCipherAsync(connectionString, cancellationToken).ConfigureAwait(false);

        DbContextOptions<PosDeviceDbContext> contextOptions = new DbContextOptionsBuilder<PosDeviceDbContext>()
            .UseSqlite(connectionString, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(PosDeviceDbContext).Assembly.FullName);
                sqlite.MigrationsHistoryTable("__migrations_history");
            })
            .Options;

        await using PosDeviceDbContext context = new(contextOptions);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a context for the already-initialized encrypted store.</summary>
    public async Task<PosDeviceDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        string connectionString = await BuildConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        DbContextOptions<PosDeviceDbContext> contextOptions = new DbContextOptionsBuilder<PosDeviceDbContext>()
            .UseSqlite(connectionString, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(PosDeviceDbContext).Assembly.FullName);
                sqlite.MigrationsHistoryTable("__migrations_history");
            })
            .Options;

        return new PosDeviceDbContext(contextOptions);
    }

    private async Task<string> BuildConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(options.DatabasePath))
        {
            throw new InvalidOperationException("The device database path must be absolute.");
        }

        string key = await keyProvider.GetDatabaseKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("The device database encryption key is unavailable.");
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            Password = key,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

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

        await using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText =
            "PRAGMA cipher_memory_security = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
        await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
