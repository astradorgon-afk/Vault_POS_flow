using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// The only writer of the device's downloaded caches, permission snapshots and
/// feed cursor (OFFLINE_SYNC.md §2 and §5).
/// </summary>
/// <remarks>
/// A page and the cursor that follows it commit in one SQLite transaction, so an
/// interrupted pull either left no trace or is recognised as already applied when
/// it is replayed. The transaction is <c>BEGIN IMMEDIATE</c>, so a second applier
/// waits for the first and then sees the cursor it committed.
/// </remarks>
public sealed class ChangeFeedApplier(DeviceDatabaseInitializer database, ISystemClock clock)
{
    /// <summary>The cursor name of the master-data change feed.</summary>
    public const string ChangeFeed = "change-feed";

    /// <summary>Reads the stored change-feed cursor; zero before the first page.</summary>
    public async Task<long> GetCursorAsync(CancellationToken cancellationToken = default)
    {
        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await ReadCursorAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies one page and advances the cursor in the same transaction.</summary>
    /// <returns>
    /// The new cursor; <c>sync.feed_page_invalid</c> for a malformed page, or
    /// <c>sync.feed_cursor_mismatch</c> when the page does not continue from the
    /// stored cursor. Database faults are thrown and roll the whole page back.
    /// </returns>
    public async Task<Result<ChangeFeedApplyOutcome>> ApplyAsync(
        ChangeFeedPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        Result validation = ChangeFeedPageValidator.Validate(page);
        if (validation.IsFailure)
        {
            return Result.Failure<ChangeFeedApplyOutcome>(validation.Error);
        }

        await using PosDeviceDbContext context =
            await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long stored = await ReadCursorAsync(context, cancellationToken).ConfigureAwait(false);
        if (page.FromCursor != stored)
        {
            return page.NextCursor <= stored
                ? Result.Success(new ChangeFeedApplyOutcome(stored, 0, AlreadyApplied: true))
                : Result.Failure<ChangeFeedApplyOutcome>(ChangeFeedErrors.CursorMismatch(stored, page.FromCursor));
        }

        using (context.ChangeFeedWrites.Open())
        {
            // Each change is saved before the next is read, so a later change in
            // the page always sees the earlier one exactly as the server ordered them.
            foreach (ChangeFeedChange change in page.Changes)
            {
                Result applied = await ApplyChangeAsync(context, change, cancellationToken).ConfigureAwait(false);

                if (applied.IsFailure)
                {
                    // Leaving the transaction uncommitted rolls the whole page
                    // back, including any change already written before this one.
                    return Result.Failure<ChangeFeedApplyOutcome>(applied.Error);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                context.ChangeTracker.Clear();
            }

            if (page.NextCursor != stored)
            {
                DeviceSyncCursor? cursor = await context.SyncCursors
                    .FindAsync([ChangeFeed], cancellationToken)
                    .ConfigureAwait(false);
                if (cursor is null)
                {
                    context.SyncCursors.Add(new DeviceSyncCursor(ChangeFeed, page.NextCursor, clock.UtcNow));
                }
                else
                {
                    cursor.Advance(page.NextCursor, clock.UtcNow);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new ChangeFeedApplyOutcome(page.NextCursor, page.Changes.Count, AlreadyApplied: false));
    }

    private static async Task<long> ReadCursorAsync(PosDeviceDbContext context, CancellationToken cancellationToken)
        => await context.SyncCursors
            .AsNoTracking()
            .Where(c => c.Feed == ChangeFeed)
            .Select(c => (long?)c.Position)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

    private static async Task<Result> ApplyChangeAsync(
        PosDeviceDbContext context,
        ChangeFeedChange change,
        CancellationToken cancellationToken)
    {
        switch (change)
        {
            case ProductChanged p:
                DeviceCachedProduct? product = await context.Products
                    .FindAsync([p.ProductId], cancellationToken).ConfigureAwait(false);
                if (product is null)
                {
                    context.Products.Add(new DeviceCachedProduct(
                        p.ProductId, p.Sku, p.Name, p.IsActive, p.TracksBatches, p.TracksExpiry,
                        p.SourceVersion, p.UpdatedAtUtc, p.IsVatExempt));
                }
                else
                {
                    product.Refresh(
                        p.Sku, p.Name, p.IsActive, p.TracksBatches, p.TracksExpiry, p.SourceVersion,
                        p.UpdatedAtUtc, p.IsVatExempt);
                }

                break;

            case BatchChanged b2:
                DeviceCachedBatch? batch = await context.Batches
                    .FindAsync([b2.BatchId], cancellationToken).ConfigureAwait(false);
                if (batch is null)
                {
                    context.Batches.Add(new DeviceCachedBatch(
                        b2.BatchId, b2.ProductId, b2.LotNumber, b2.ReceivedOn, b2.ExpiresOn, b2.UnitCost));
                }
                else
                {
                    batch.Refresh(b2.ProductId, b2.LotNumber, b2.ReceivedOn, b2.ExpiresOn, b2.UnitCost);
                }

                break;

            case ProductBarcodeChanged b:
                DeviceCachedProductBarcode? barcode = await context.ProductBarcodes
                    .FindAsync([b.Barcode], cancellationToken).ConfigureAwait(false);
                if (barcode is null)
                {
                    context.ProductBarcodes.Add(new DeviceCachedProductBarcode(b.Barcode, b.ProductId, b.IsPrimary, b.IsActive));
                }
                else
                {
                    barcode.Refresh(b.ProductId, b.IsPrimary, b.IsActive);
                }

                break;

            case ProductPriceChanged p:
                DeviceCachedProductPrice? price = await context.ProductPrices
                    .FindAsync([p.PriceId], cancellationToken).ConfigureAwait(false);
                if (price is null)
                {
                    context.ProductPrices.Add(new DeviceCachedProductPrice(
                        p.PriceId, p.ProductId, p.LocationId, p.Amount, p.Currency, p.EffectiveFromUtc, p.EffectiveToUtc));
                }
                else
                {
                    price.Refresh(p.ProductId, p.LocationId, p.Amount, p.Currency, p.EffectiveFromUtc, p.EffectiveToUtc);
                }

                break;

            case ProductPriceRemoved r:
                await context.ProductPrices
                    .Where(x => x.Id == r.PriceId)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                break;

            case LocationChanged l:
                DeviceCachedLocation? location = await context.Locations
                    .FindAsync([l.LocationId], cancellationToken).ConfigureAwait(false);
                if (location is null)
                {
                    context.Locations.Add(new DeviceCachedLocation(
                        l.LocationId, l.Code, l.Name, l.Kind, l.TimeZoneId, l.CurrencyCode, l.IsActive,
                        l.SettingsJson));
                }
                else
                {
                    location.Refresh(l.Code, l.Name, l.Kind, l.TimeZoneId, l.CurrencyCode, l.IsActive, l.SettingsJson);
                }

                break;

            case UserChanged u:
                DeviceCachedUser? user = await context.Users
                    .FindAsync([u.UserId], cancellationToken).ConfigureAwait(false);
                if (user is null)
                {
                    context.Users.Add(new DeviceCachedUser(u.UserId, u.UserName, u.DisplayName, u.IsActive, u.SecurityVersion));
                }
                else
                {
                    user.Refresh(u.UserName, u.DisplayName, u.IsActive, u.SecurityVersion);
                }

                break;

            case PermissionSnapshotIssued s:
                // Policy versions only ever increase. An older one is a replayed
                // or forged page trying to restore authority the server has since
                // narrowed, so it fails the page rather than replacing what is
                // stored. An equal version is the ordinary re-issue — a refreshed
                // expiry on the same policy — and is applied.
                long? held = await context.PermissionSnapshots
                    .AsNoTracking()
                    .Where(x => x.UserId == s.UserId)
                    .Select(x => (long?)x.PolicyVersion)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (held is { } stored && s.PolicyVersion < stored)
                {
                    return Result.Failure(
                        ChangeFeedErrors.SnapshotRollback(stored, s.PolicyVersion, s.Sequence));
                }

                await DeleteSnapshotAsync(context, s.UserId, cancellationToken).ConfigureAwait(false);
                foreach (PermissionSnapshotGrant grant in s.Grants)
                {
                    context.PermissionSnapshots.Add(new DevicePermissionSnapshot(
                        Guid.CreateVersion7(), s.UserId, grant.Permission, grant.LocationId,
                        s.PolicyVersion, s.IssuedAtUtc, s.ExpiresAtUtc));
                }

                break;

            case PermissionSnapshotRevoked r:
                await DeleteSnapshotAsync(context, r.UserId, cancellationToken).ConfigureAwait(false);
                break;

            case SyncRetryRequested retry:
                await ReopenAsync(context, retry, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException("Validated pages contain only supported change kinds.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Puts one refused event back in the queue, because somebody changed the
    /// thing that made the server refuse it.
    /// </summary>
    /// <remarks>
    /// Only an event that stopped moving is reopened. One still waiting its turn
    /// is left alone: resetting its attempt count would throw away a backoff the
    /// device is in the middle of, and one already accepted is not reopened at
    /// all — asking a register to send a sale head office already holds is how a
    /// day's takings get counted twice.
    /// </remarks>
    private static async Task<int> ReopenAsync(
        PosDeviceDbContext context,
        SyncRetryRequested retry,
        CancellationToken cancellationToken)
        => await context.Outbox
            .Where(e => e.EventId == retry.EventId
                        && e.DeviceId == retry.DeviceId
                        && (e.Status == OutboxStatus.Rejected
                            || e.Status == OutboxStatus.Failed
                            || e.Status == OutboxStatus.Conflict))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(e => e.Status, OutboxStatus.Pending)
                    .SetProperty(e => e.NextRetryAtUtc, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);

    private static Task<int> DeleteSnapshotAsync(
        PosDeviceDbContext context,
        UserId userId,
        CancellationToken cancellationToken)
        => context.PermissionSnapshots.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
}
