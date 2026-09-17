using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>A migrated, encrypted device database in its own temporary directory.</summary>
internal sealed class TemporaryDeviceDatabase : IAsyncDisposable
{
    /// <summary>A fixed 256-bit test key: bytes 1 to 32.</summary>
    public static readonly byte[] EncryptionKey = [.. Enumerable.Range(1, DeviceDatabaseInitializer.KeyLengthBytes).Select(i => (byte)i)];

    public static readonly DateTimeOffset Now = new(2026, 9, 17, 2, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "vaultflow-device-tests", Guid.NewGuid().ToString("N"));

    private TemporaryDeviceDatabase()
    {
        Initializer = new DeviceDatabaseInitializer(
            new DeviceDatabaseOptions(DatabasePath), new FixedKeyProvider(EncryptionKey));
        Clock = new MovableClock(Now);
        Applier = new ChangeFeedApplier(Initializer, Clock);
        Profiles = new DeviceProfileAccessor(Initializer);
    }

    public string DatabasePath => Path.Combine(_directory, "device.db");

    public DeviceDatabaseInitializer Initializer { get; }

    public ChangeFeedApplier Applier { get; }

    /// <summary>The device clock, movable so expiry can be crossed without waiting.</summary>
    public MovableClock Clock { get; }

    /// <summary>Reads the enrolled profile, as the numbering adapter does.</summary>
    public IDeviceProfileAccessor Profiles { get; }

    /// <summary>A keyed connection with no device context behind it, as raw tooling would open.</summary>
    public string EncryptedConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Password = DeviceDatabaseInitializer.RawKey(EncryptionKey),
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

    /// <summary>Writes the device profile that enrolment would write.</summary>
    public async Task EnrolAsync(string shortCode, LocationId? locationId = null)
    {
        await using PosDeviceDbContext context = await OpenContextAsync();
        context.DeviceProfiles.Add(new DeviceStoreProfile(
            DeviceId.New(), locationId ?? LocationId.New(), shortCode, Now));
        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Creates a generator bound to a fresh context, as a scope would.</summary>
    public async Task<(DeviceDocumentNumberGenerator Generator, PosDeviceDbContext Context)> OpenGeneratorAsync()
    {
        PosDeviceDbContext context = await OpenContextAsync();
        return (new DeviceDocumentNumberGenerator(context, Profiles, Clock), context);
    }

    /// <summary>Creates a permission evaluator over this device's snapshot.</summary>
    public DeviceSnapshotPermissionEvaluator CreateEvaluator()
        => new(Initializer, Clock);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Hands out a copy each time, because the initializer clears the key it receives.</summary>
    private sealed class FixedKeyProvider(byte[] key) : IDeviceDatabaseKeyProvider
    {
        public ValueTask<byte[]> GetDatabaseKeyAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(key.ToArray());
    }

    /// <summary>A clock the test moves, so snapshot expiry is crossed deliberately.</summary>
    internal sealed class MovableClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public DateOnly BusinessDateFor(string timeZoneId) => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }
}
