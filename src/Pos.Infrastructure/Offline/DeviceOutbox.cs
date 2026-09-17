using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Queues the business events a device has produced but not yet uploaded.
/// </summary>
/// <remarks>
/// A device is an event producer, not a database replica (OFFLINE_SYNC.md), so
/// what is queued is "a shift opened", never "a row changed". The device
/// repositories enqueue: they are the adapters that know which business event
/// just happened, and enqueuing there keeps the shared handlers — the ones the
/// server also runs — free of any knowledge that an outbox exists.
/// </remarks>
public interface IDeviceOutbox
{
    /// <summary>
    /// Queues one event in the caller's unit of work, so it commits with the
    /// business rows it describes or not at all.
    /// </summary>
    /// <typeparam name="TPayload">The payload type.</typeparam>
    /// <param name="type">The kind of event.</param>
    /// <param name="payload">What happened, serialized canonically.</param>
    /// <param name="locationId">The location it applies to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The queued event.</returns>
    Task<OutboxEvent> EnqueueAsync<TPayload>(
        SyncEventType type,
        TPayload payload,
        LocationId locationId,
        CancellationToken cancellationToken);

    /// <summary>Counts events still waiting to reach head office.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many events are pending or being retried.</returns>
    Task<int> CountUnsentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The device's outbox, over its encrypted store.
/// </summary>
/// <param name="context">The scoped device context.</param>
/// <param name="currentUser">Who produced the event.</param>
/// <param name="profile">This device's enrolled identity.</param>
/// <param name="clock">The device clock.</param>
public sealed class DeviceOutbox(
    PosDeviceDbContext context,
    ICurrentUser currentUser,
    IDeviceProfileAccessor profile,
    ISystemClock clock) : IDeviceOutbox
{
    /// <summary>The single-row counter that orders this device's events.</summary>
    public const string SequenceName = "outbox";

    /// <inheritdoc />
    public async Task<OutboxEvent> EnqueueAsync<TPayload>(
        SyncEventType type,
        TPayload payload,
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        DeviceStoreProfile enrolled = await profile.GetAsync(cancellationToken).ConfigureAwait(false);

        long sequence = await NextSequenceAsync(cancellationToken).ConfigureAwait(false);

        OutboxEvent queued = new(
            EventId.New(),
            sequence,
            type,
            CanonicalJson.Serialize(payload),
            enrolled.DeviceId,
            currentUser.UserId ?? UserId.Empty,
            locationId,
            clock.UtcNow,
            // Monotonic: it keeps increasing across a wall-clock change, so a
            // device whose clock was moved backwards still produces events in an
            // order the server can see through.
            Environment.TickCount64,
            currentUser.CorrelationId);

        await context.Outbox.AddAsync(queued, cancellationToken).ConfigureAwait(false);
        return queued;
    }

    /// <inheritdoc />
    public Task<int> CountUnsentAsync(CancellationToken cancellationToken)
        => context.Outbox
            .AsNoTracking()
            .CountAsync(
                e => e.Status != OutboxStatus.Synchronized && e.Status != OutboxStatus.Conflict,
                cancellationToken);

    /// <summary>
    /// Allocates the next sequence value in the caller's transaction, so a
    /// rolled-back event releases its number and the sequence stays gapless.
    /// </summary>
    private async Task<long> NextSequenceAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = Upsert;

        DbParameter name = command.CreateParameter();
        name.ParameterName = "name";
        name.Value = SequenceName;
        command.Parameters.Add(name);

        object? allocated = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // A fresh row starts at two so the first allocated value is one, the
        // same shape the document counters use.
        return Convert.ToInt64(allocated, CultureInfo.InvariantCulture) - 1;
    }

    private const string Upsert =
        "INSERT INTO device_sequence (name, next_value) VALUES (@name, 2) " +
        "ON CONFLICT (name) DO UPDATE SET next_value = next_value + 1 " +
        "RETURNING next_value";
}
