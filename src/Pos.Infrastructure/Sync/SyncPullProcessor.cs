using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Infrastructure.Persistence;
using Pos.Shared.Sync;

namespace Pos.Infrastructure.Sync;

/// <summary>Why a device cannot be served a page and must start again.</summary>
public enum SyncPullRefusal
{
    /// <summary>The device was served.</summary>
    None = 0,

    /// <summary>The device is not enrolled, or its registration is gone.</summary>
    DeviceUnknown = 1,

    /// <summary>
    /// The server can no longer prove it holds everything the device missed, so
    /// the only honest answer is a fresh baseline.
    /// </summary>
    RebaselineRequired = 2,
}

/// <summary>One answer to a pull, which is either a page or a refusal.</summary>
/// <param name="Refusal">Why nothing was served, or <see cref="SyncPullRefusal.None"/>.</param>
/// <param name="Page">The page, when there was one.</param>
public sealed record SyncPullResult(SyncPullRefusal Refusal, SyncPullResponse? Page)
{
    /// <summary>A served page.</summary>
    /// <param name="page">The page.</param>
    /// <returns>The result.</returns>
    public static SyncPullResult Served(SyncPullResponse page) => new(SyncPullRefusal.None, page);

    /// <summary>A refusal.</summary>
    /// <param name="refusal">Why.</param>
    /// <returns>The result.</returns>
    public static SyncPullResult Refused(SyncPullRefusal refusal) => new(refusal, null);
}

/// <summary>
/// Serves one page of the change feed to one device (OFFLINE_SYNC.md §5).
/// </summary>
/// <remarks>
/// <para>
/// A device is served the changes that are everybody's and the changes that are
/// its own store's, and nothing else. The scope filter is not a convenience: a
/// register in one shop has no business holding another shop's prices, and a
/// feed that shipped them would make every till a copy of the whole estate.
/// </para>
/// <para>
/// The page's <c>nextCursor</c> can run ahead of the last change it carries.
/// That is the point of returning it rather than letting the device infer one:
/// the server skipped the changes outside this device's scope, and a device that
/// stopped at the last change it was given would rescan that gap on every pull
/// for ever.
/// </para>
/// </remarks>
/// <param name="context">The server database.</param>
public sealed class SyncPullProcessor(PosDbContext context)
{
    /// <summary>The most changes one page carries.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Serves a page, or says why it cannot.</summary>
    /// <param name="deviceId">The asking device.</param>
    /// <param name="cursor">The sequence it last stored.</param>
    /// <param name="limit">How many changes it will take.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page or the refusal.</returns>
    public async Task<SyncPullResult> PullAsync(
        DeviceId deviceId,
        long cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        LocationId? scope = await context.Set<Device>()
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => (LocationId?)d.LocationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (scope is not { } locationId)
        {
            return SyncPullResult.Refused(SyncPullRefusal.DeviceUnknown);
        }

        int take = Math.Clamp(limit, 1, MaxPageSize);
        long from = Math.Max(cursor, 0);

        // Projected to a nullable so an empty feed comes back as null rather
        // than throwing; the provider translates this and not DefaultIfEmpty.
        long highest = await context.ChangeFeed
            .AsNoTracking()
            .MaxAsync(e => (long?)e.Sequence, cancellationToken)
            .ConfigureAwait(false) ?? 0L;

        if (from > highest)
        {
            // The device is ahead of the feed. Either it kept a cursor from a
            // server that has since been restored from a backup, or the cursor
            // is not this feed's at all. Serving from zero would silently replay
            // changes it already applied, so it starts again instead.
            return SyncPullResult.Refused(SyncPullRefusal.RebaselineRequired);
        }

        if (from > 0)
        {
            long earliest = await context.ChangeFeed
                .AsNoTracking()
                .MinAsync(e => (long?)e.Sequence, cancellationToken)
                .ConfigureAwait(false) ?? 0L;

            // Everything after the cursor must still be here. Once the feed is
            // pruned, a device that was dark longer than the retention window
            // asks for changes the server threw away — and the server must say
            // so rather than serve a page with a hole in it.
            if (earliest > from + 1)
            {
                return SyncPullResult.Refused(SyncPullRefusal.RebaselineRequired);
            }
        }

        List<ChangeFeedEntry> rows = await context.ChangeFeed
            .AsNoTracking()
            .Where(e => e.Sequence > from
                        && (e.LocationScopeId == null || e.LocationScopeId == locationId.Value))
            .OrderBy(e => e.Sequence)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A full page stops at its last change, because there may be more behind
        // it. A page that did not fill has reached the end of the feed, so the
        // cursor moves to the end — past everything skipped for being another
        // store's business.
        long next = rows.Count == take && rows.Count > 0 ? rows[^1].Sequence : highest;

        return SyncPullResult.Served(new SyncPullResponse(
            from,
            next,
            [.. rows.Select(e => new SyncPullChange(e.Kind, JsonDocument.Parse(e.PayloadJson).RootElement.Clone()))]));
    }
}
