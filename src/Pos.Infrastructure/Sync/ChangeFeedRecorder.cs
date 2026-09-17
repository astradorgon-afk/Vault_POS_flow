using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Catalog;
using Pos.Domain.Inventory;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Appends a change-feed row for every change a device would need to hear
/// about, in the same transaction as the change itself.
/// </summary>
/// <remarks>
/// <para>
/// An interceptor rather than a call in each handler, for the same reason the
/// ledger uses one: a feed that depends on somebody remembering is a feed that
/// silently stops carrying the thing nobody remembered. A product added through
/// a path nobody thought about still reaches the registers.
/// </para>
/// <para>
/// Because it writes in the caller's transaction, a change that rolls back
/// takes its feed row with it. There is no window in which a device can be told
/// about a price that does not exist.
/// </para>
/// <para>
/// Sequences come from a single counter row, allocated inside that same
/// transaction. That serialises feed appends against each other, which is the
/// price of the ordering the cursor depends on: if a row numbered 40 could
/// commit before one numbered 39, a device would store 40 and never see 39
/// again. Master data changes rarely, and a sale does not touch this table.
/// </para>
/// </remarks>
/// <param name="clock">The authoritative clock.</param>
public sealed class ChangeFeedRecorder(ISystemClock clock) : SaveChangesInterceptor
{
    /// <summary>The counter row the feed's order comes from.</summary>
    public const string SequenceName = "change_feed";

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new StronglyTypedIdJsonConverter() },
    };

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is PosDbContext context)
        {
            await RecordAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordAsync(PosDbContext context, CancellationToken cancellationToken)
    {
        List<EntityEntry> changed =
        [
            .. context.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted),
        ];

        if (changed.Count == 0)
        {
            return;
        }

        // Read only when there is something to record, which for master data is
        // rare. The currency lives on the organization, not on each location, so
        // the feed carries it down rather than each register assuming one.
        string currency = await context.Organizations
            .AsNoTracking()
            .Select(o => o.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;

        List<PendingChange> pending = [];

        foreach (EntityEntry entry in changed)
        {
            PendingChange? change = Describe(entry, currency);

            if (change is not null)
            {
                pending.Add(change);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        long first = await AllocateAsync(context, pending.Count, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = clock.UtcNow;

        for (int index = 0; index < pending.Count; index++)
        {
            PendingChange change = pending[index];
            long sequence = first + index;

            context.ChangeFeed.Add(new ChangeFeedEntry(
                sequence,
                change.Kind,
                change.LocationScopeId,
                JsonSerializer.Serialize(change.Payload(sequence), change.PayloadType, PayloadOptions),
                now));
        }
    }

    /// <summary>
    /// Turns a tracked entity into the change a device receives, or null when it
    /// is not something a device holds.
    /// </summary>
    /// <remarks>
    /// Deliberately exhaustive rather than reflective. A device's cache is a
    /// deliberate subset — it holds products and prices, not purchase orders —
    /// and a reflective rule would start shipping whatever was added next.
    /// </remarks>
    private static PendingChange? Describe(EntityEntry entry, string currency)
    {
        // A deletion tells a register to forget something, which is a different
        // message from a change. Only a cancelled future price is ever deleted —
        // nothing else a device caches is removed rather than deactivated — and a
        // register that never hears it goes on selling at a price the server no
        // longer holds.
        if (entry.State == EntityState.Deleted)
        {
            return entry.Entity is ProductPrice removed
                ? new PendingChange(
                    nameof(ProductPriceRemoved),
                    removed.LocationId?.Value,
                    typeof(ProductPriceRemoved),
                    sequence => new ProductPriceRemoved(sequence, removed.Id))
                : null;
        }

        return entry.Entity switch
        {
            Product product => new PendingChange(
                nameof(ProductChanged),
                null,
                typeof(ProductChanged),
                sequence => new ProductChanged(
                    sequence,
                    product.Id,
                    product.Sku.Value,
                    product.Name,
                    product.IsActive,
                    product.TracksBatches,
                    product.TracksExpiry,

                    // The feed's own sequence is the source version. It is monotonic
                    // by construction and says exactly what the cache needs to know:
                    // which server state this row came from.
                    sequence,
                    product.UpdatedAtUtc,
                    product.IsVatExempt)),

            ProductPrice price => new PendingChange(
                nameof(ProductPriceChanged),
                price.LocationId?.Value,
                typeof(ProductPriceChanged),
                sequence => new ProductPriceChanged(
                    sequence,
                    price.Id,
                    price.ProductId,
                    price.LocationId,
                    price.Amount,
                    currency,
                    price.EffectiveFromUtc,
                    price.EffectiveToUtc)),

            Batch batch => new PendingChange(
                nameof(BatchChanged),
                null,
                typeof(BatchChanged),
                sequence => new BatchChanged(
                    sequence,
                    batch.Id,
                    batch.ProductId,
                    batch.LotNumber,
                    batch.ReceivedOn,
                    batch.ExpiresOn,
                    batch.UnitCost)),

            ProductBarcode barcode => new PendingChange(
                nameof(ProductBarcodeChanged),
                null,
                typeof(ProductBarcodeChanged),
                sequence => new ProductBarcodeChanged(
                    sequence,
                    barcode.Value,
                    barcode.ProductId,
                    barcode.IsPrimary,
                    barcode.RetiredAtUtc is null)),

            Location location => new PendingChange(
                nameof(LocationChanged),
                location.Id.Value,
                typeof(LocationChanged),
                sequence => new LocationChanged(
                    sequence,
                    location.Id,
                    location.Code,
                    location.Name,
                    location.Kind,
                    location.TimeZoneId,
                    currency,
                    location.IsActive,
                    (location.Settings ?? LocationSettings.Default).ToJson())),

            _ => null,
        };
    }

    /// <summary>
    /// Takes the next <paramref name="count"/> sequence values in the caller's
    /// transaction, so a rolled-back change releases its numbers.
    /// </summary>
    private static async Task<long> AllocateAsync(
        PosDbContext context,
        int count,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        // Assigned from a constant on each branch rather than through a variable,
        // so it is visibly not built from anything a caller supplied. The count
        // and the name travel as parameters.
        if (context.Database.IsSqlite())
        {
            command.CommandText = SqliteUpsert;
        }
        else
        {
            command.CommandText = PostgresUpsert;
        }

        DbParameter name = command.CreateParameter();
        name.ParameterName = "name";
        name.Value = SequenceName;
        command.Parameters.Add(name);

        DbParameter take = command.CreateParameter();
        take.ParameterName = "count";
        take.Value = count;
        command.Parameters.Add(take);

        object? allocated = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // The row starts one past what it hands out, as the document counters
        // do, so the first sequence ever allocated is one.
        return Convert.ToInt64(allocated, CultureInfo.InvariantCulture) - count;
    }

    private const string PostgresUpsert =
        "INSERT INTO sync.feed_sequence (name, next_value) VALUES (@name, 1 + @count) " +
        "ON CONFLICT (name) DO UPDATE SET next_value = f.next_value + @count " +
        "WHERE f.name = @name RETURNING next_value";

    private const string SqliteUpsert =
        "INSERT INTO feed_sequence (name, next_value) VALUES (@name, 1 + @count) " +
        "ON CONFLICT (name) DO UPDATE SET next_value = next_value + @count " +
        "RETURNING next_value";

    /// <summary>A change waiting for its number.</summary>
    private sealed record PendingChange(
        string Kind,
        Guid? LocationScopeId,
        Type PayloadType,
        Func<long, ChangeFeedChange> Payload);
}
