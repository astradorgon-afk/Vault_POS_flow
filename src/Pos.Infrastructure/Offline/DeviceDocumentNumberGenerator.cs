using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Allocates this device's own SAL, RET and SHF numbers from the encrypted
/// device database, so a sale rung up with no network keeps the number printed
/// on its receipt (POS.md §11, OFFLINE_SYNC.md §4).
/// </summary>
/// <remarks>
/// <para>
/// It is the mirror image of the server's generator, and the two refuse opposite
/// halves of the port. The server refuses device-scoped types because it cannot
/// know a device's sequence; this refuses central types because a device may
/// never mint a purchase order or a transfer number — those stay the server's,
/// and an offline device queues the request instead.
/// </para>
/// <para>
/// The allocation is one atomic upsert, and it joins the caller's transaction
/// when there is one, so a sale that rolls back releases its number rather than
/// leaving a gap. Without a transaction the allocation commits on its own: a
/// crash between allocation and the sale burns a number, which is the correct
/// trade — a receipt number is never reused.
/// </para>
/// </remarks>
/// <param name="context">The device database context.</param>
/// <param name="profile">This device's enrolled identity.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceDocumentNumberGenerator(
    PosDeviceDbContext context,
    IDeviceProfileAccessor profile,
    ISystemClock clock) : IDocumentNumberGenerator
{
    /// <summary>
    /// Allocates the next number for a device-scoped type using this device's
    /// own short code.
    /// </summary>
    /// <param name="type">The document type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The allocated number.</returns>
    /// <exception cref="NotSupportedException">The type is numbered centrally.</exception>
    public async Task<DocumentNumber> NextAsync(DocumentType type, CancellationToken cancellationToken)
    {
        DeviceStoreProfile enrolled = await profile.GetAsync(cancellationToken).ConfigureAwait(false);
        return await AllocateAsync(type, enrolled.ShortCode, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocumentNumber> NextScopedAsync(
        DocumentType type,
        string deviceShortCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceShortCode))
        {
            throw new ArgumentException("A device short code is required.", nameof(deviceShortCode));
        }

        DeviceStoreProfile enrolled = await profile.GetAsync(cancellationToken).ConfigureAwait(false);
        string requested = deviceShortCode.Trim().ToUpperInvariant();

        // A device numbers for itself and nothing else. Minting under another
        // device's code would collide with that device's own sequence, and the
        // server would accept it: the posted number's code would match a real
        // device.
        if (!string.Equals(requested, enrolled.ShortCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"This device is {enrolled.ShortCode} and cannot allocate numbers for {requested}."));
        }

        return await AllocateAsync(type, enrolled.ShortCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentNumber> AllocateAsync(
        DocumentType type,
        string shortCode,
        CancellationToken cancellationToken)
    {
        if (!DocumentNumber.IsDeviceScoped(type))
        {
            throw new NotSupportedException(
                FormattableString.Invariant(
                    $"{type}: a device allocates only device-scoped numbers; this one is the server's to issue."));
        }

        int year = clock.UtcNow.Year;
        string periodKey = year.ToString("D4", CultureInfo.InvariantCulture);

        DbConnection connection = context.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = Upsert;
        command.Parameters.Add(Parameter(command, "type", (short)type));
        command.Parameters.Add(Parameter(command, "period", periodKey));

        object? allocated = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long nextValue = Convert.ToInt64(allocated, CultureInfo.InvariantCulture);

        return DocumentNumber.CreateForDevice(type, year, shortCode, nextValue - 1);
    }

    // A fresh row starts at two so the allocated sequence (the returned value
    // minus one) begins at one, exactly as the server's counter does.
    private const string Upsert =
        "INSERT INTO document_counter (document_type, period_key, next_value) " +
        "VALUES (@type, @period, 2) " +
        "ON CONFLICT (document_type, period_key) " +
        "DO UPDATE SET next_value = next_value + 1 " +
        "RETURNING next_value";

    private static DbParameter Parameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }
}

/// <summary>
/// Reads this device's enrolled identity. It is a port so the numbering and
/// permission adapters do not each re-read the profile row, and so tests can
/// enrol a device without a platform secure store.
/// </summary>
public interface IDeviceProfileAccessor
{
    /// <summary>Gets the enrolled profile.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="InvalidOperationException">The device is not enrolled.</exception>
    Task<DeviceStoreProfile> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the profile from the device database once per process. A device is
/// enrolled once and its identity is immutable, so re-reading it on every sale
/// would be a per-connection cost for a value that cannot change.
/// </summary>
/// <param name="database">The encrypted device store.</param>
public sealed class DeviceProfileAccessor(DeviceDatabaseInitializer database) : IDeviceProfileAccessor
{
    private readonly Lock gate = new();
    private Task<DeviceStoreProfile>? profile;

    /// <inheritdoc />
    public Task<DeviceStoreProfile> GetAsync(CancellationToken cancellationToken)
    {
        Task<DeviceStoreProfile> pending;

        lock (this.gate)
        {
            if (this.profile is null || this.profile.IsFaulted || this.profile.IsCanceled)
            {
                this.profile = ReadAsync();
            }

            pending = this.profile;
        }

        return pending.WaitAsync(cancellationToken);
    }

    private async Task<DeviceStoreProfile> ReadAsync()
    {
        await using PosDeviceDbContext context = await database.CreateDbContextAsync().ConfigureAwait(false);

        return await context.DeviceProfiles.AsNoTracking().SingleOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "This device is not enrolled, so it cannot allocate document numbers or evaluate permissions.");
    }
}
