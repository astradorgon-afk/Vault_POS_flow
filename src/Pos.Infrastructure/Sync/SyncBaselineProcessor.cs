using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Offline;
using Pos.Infrastructure.Persistence;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Sync;

/// <summary>
/// Builds a register's whole starting state.
/// </summary>
/// <remarks>
/// <para>
/// The change feed carries changes, not a starting state. A register provisioned
/// today has no rows to read for anything that existed before the feed did, so
/// pulling from cursor zero would leave it without its catalogue, its store's
/// settings or the counterparty every sale posts against. This is what it fetches
/// instead, and what a <c>410 Gone</c> sends it back for.
/// </para>
/// <para>
/// It is deliberately the **same shape as a page**: current state projected as
/// the very change records the feed carries, so the device writes it with the
/// applier it already has rather than a second code path that could disagree
/// about what a product is.
/// </para>
/// <para>
/// Most of it is projected from the tables the server keeps. Permission snapshots
/// are the exception: the server issues one and keeps no copy, so the feed itself
/// is where they live, and the baseline reads the live ones back out of it. Without
/// that, a baseline would carry a register past the row granting its cashier the
/// authority to sell and leave it unable to ring anything up.
/// </para>
/// <para>
/// The cursor it comes back with is the feed's high-water mark read <b>before</b>
/// the state is projected, never after. A change that commits while the baseline
/// is being built is then both inside the state and after the cursor, so the
/// device applies it a second time; reading the mark afterwards would let that
/// same change fall between the two and be stepped over for ever. Repeating an
/// idempotent write costs nothing. Missing one costs a register its catalogue.
/// </para>
/// </remarks>
/// <param name="context">The server database.</param>
public sealed class SyncBaselineProcessor(PosDbContext context)
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new StronglyTypedIdJsonConverter() },
    };

    /// <summary>Builds the baseline for one device.</summary>
    /// <param name="deviceId">The asking device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The baseline, or null when the device is not registered.</returns>
    public async Task<SyncBaselineResponse?> BuildAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        LocationId? scope = await context.Set<Device>()
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => (LocationId?)d.LocationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (scope is not { } locationId)
        {
            return null;
        }

        // Read first, project second. The order is the whole guarantee.
        long resumeCursor = await context.ChangeFeed
            .AsNoTracking()
            .MaxAsync(e => (long?)e.Sequence, cancellationToken)
            .ConfigureAwait(false) ?? 0L;

        List<(string Kind, Func<long, ChangeFeedChange> Change)> parts = [];

        // The register's own store, and the counterparties every sale posts its
        // other leg against. No other store: a register holds its own.
        List<Location> locations = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId || l.Kind == LocationKind.External)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (Location location in locations)
        {
            parts.Add((nameof(LocationChanged), sequence => new LocationChanged(
                sequence,
                location.Id,
                location.Code,
                location.Name,
                location.Kind,
                location.TimeZoneId,
                Money.DefaultCurrency,
                location.IsActive,
                (location.Settings ?? LocationSettings.Default).ToJson())));
        }

        List<Product> products = await context.Products
            .AsNoTracking()
            .Include(p => p.Barcodes)
            .Include(p => p.Prices)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (Product product in products)
        {
            parts.Add((nameof(ProductChanged), sequence => new ProductChanged(
                sequence,
                product.Id,
                product.Sku.Value,
                product.Name,
                product.IsActive,
                product.TracksBatches,
                product.TracksExpiry,

                // The server state this row came from, which is the cursor the
                // baseline was read at — not its position within the baseline.
                resumeCursor,
                product.UpdatedAtUtc,
                product.IsVatExempt)));

            foreach (ProductBarcode barcode in product.Barcodes)
            {
                parts.Add((nameof(ProductBarcodeChanged), sequence => new ProductBarcodeChanged(
                    sequence, barcode.Value, barcode.ProductId, barcode.IsPrimary, barcode.RetiredAtUtc is null)));
            }

            // Prices that could still apply. A period that closed before now can
            // never price a sale, and a baseline is a starting state rather than
            // a history: sending the lot would grow without bound.
            foreach (ProductPrice price in product.Prices
                .Where(p => p.LocationId is null || p.LocationId == locationId)
                .Where(p => p.EffectiveToUtc is null || p.EffectiveToUtc > DateTimeOffset.UtcNow))
            {
                parts.Add((nameof(ProductPriceChanged), sequence => new ProductPriceChanged(
                    sequence,
                    price.Id,
                    price.ProductId,
                    price.LocationId,
                    price.Amount,
                    price.Price.Currency,
                    price.EffectiveFromUtc,
                    price.EffectiveToUtc)));
            }
        }

        foreach (PermissionSnapshotIssued snapshot in await LiveSnapshotsAsync(
            locationId, resumeCursor, cancellationToken).ConfigureAwait(false))
        {
            parts.Add((nameof(PermissionSnapshotIssued), sequence => snapshot with { Sequence = sequence }));
        }

        List<Batch> batches = await context.Batches
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (Batch batch in batches)
        {
            parts.Add((nameof(BatchChanged), sequence => new BatchChanged(
                sequence,
                batch.Id,
                batch.ProductId,
                batch.LotNumber,
                batch.ReceivedOn,
                batch.ExpiresOn,
                batch.UnitCost)));
        }

        // Numbered from one. These sequences order the baseline's own rows and
        // are not feed positions: the feed's counter is left alone, because a
        // baseline records nothing that happened.
        List<SyncPullChange> changes = new(parts.Count);

        for (int index = 0; index < parts.Count; index++)
        {
            long sequence = index + 1;
            (string kind, Func<long, ChangeFeedChange> change) = parts[index];
            ChangeFeedChange built = change(sequence);

            changes.Add(new SyncPullChange(
                kind,
                JsonSerializer.SerializeToElement(built, built.GetType(), PayloadOptions)));
        }

        return new SyncBaselineResponse(resumeCursor, changes);
    }

    /// <summary>
    /// Reads back the permission snapshots that are still worth having.
    /// </summary>
    /// <remarks>
    /// One per user: the newest issue at or before the cursor, dropped if it has
    /// since been revoked or has already expired. A snapshot the device would
    /// refuse on arrival is not worth the bytes, and one it would accept over a
    /// newer grant would be worse than nothing.
    /// </remarks>
    private async Task<List<PermissionSnapshotIssued>> LiveSnapshotsAsync(
        LocationId locationId,
        long resumeCursor,
        CancellationToken cancellationToken)
    {
        List<ChangeFeedEntry> rows = await context.ChangeFeed
            .AsNoTracking()
            .Where(e => e.Sequence <= resumeCursor
                        && (e.LocationScopeId == null || e.LocationScopeId == locationId.Value)
                        && (e.Kind == nameof(PermissionSnapshotIssued)
                            || e.Kind == nameof(PermissionSnapshotRevoked)))
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<UserId, PermissionSnapshotIssued> live = [];

        foreach (ChangeFeedEntry row in rows)
        {
            if (row.Kind == nameof(PermissionSnapshotRevoked))
            {
                PermissionSnapshotRevoked? revoked = JsonSerializer
                    .Deserialize<PermissionSnapshotRevoked>(row.PayloadJson, PayloadOptions);

                if (revoked is not null)
                {
                    live.Remove(revoked.UserId);
                }

                continue;
            }

            PermissionSnapshotIssued? issued = JsonSerializer
                .Deserialize<PermissionSnapshotIssued>(row.PayloadJson, PayloadOptions);

            // Later issues replace earlier ones, which is what the device does
            // with them too — the rows arrive in order, so the last one wins.
            if (issued is not null && issued.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                live[issued.UserId] = issued;
            }
        }

        return [.. live.Values];
    }
}
