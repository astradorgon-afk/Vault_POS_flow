using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Identity;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Configuration;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>Builds a device-scoped master-data baseline for re-enrolment.</summary>
public sealed class SyncBaselineService(
    PosDbContext context,
    ICurrentUser currentUser,
    ISystemClock clock,
    DatabasePermissionEvaluator authorization,
    IPolicyVersionProvider policyVersion,
    IOptions<SecurityOptions> security)
{
    public async Task<SyncBaselineResponse> GetAsync(CancellationToken cancellationToken)
    {
        DeviceId? deviceId = currentUser.DeviceId;
        if (deviceId is null || currentUser.UserId is null)
        {
            return SyncBaselineResponse.Refused("sync.device_binding", "A device-bound session is required.");
        }

        Device? device = await context.Devices
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (device is null || !device.IsOperational)
        {
            return SyncBaselineResponse.Refused("sync.device_not_operational", "The device is not permitted to synchronize.");
        }

        List<SyncBaselineItem> items = [];
        List<ProductBaselineRow> products = await context.Products
            .AsNoTracking()
            .Select(p => new ProductBaselineRow(
                p.Id.Value, p.Sku.Value, p.Name, p.IsActive, p.TracksBatches, p.TracksExpiry, p.UpdatedAtUtc,
                p.BaseUnitOfMeasureId.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (ProductBaselineRow product in products)
        {
            items.Add(Item("ProductChanged", product.ProductId, new
            {
                productId = product.ProductId,
                sku = product.Sku,
                name = product.Name,
                isActive = product.IsActive,
                tracksBatches = product.TracksBatches,
                tracksExpiry = product.TracksExpiry,
                sourceVersion = 0L,
                updatedAtUtc = product.UpdatedAtUtc,
                baseUnitOfMeasureId = product.BaseUnitOfMeasureId,
            }));
        }

        List<BarcodeBaselineRow> barcodes = await context.ProductBarcodes
            .AsNoTracking()
            .Select(b => new BarcodeBaselineRow(
                b.Value, b.ProductId.Value, b.IsPrimary, b.RetiredAtUtc == null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (BarcodeBaselineRow barcode in barcodes)
        {
            items.Add(Item("ProductBarcodeChanged", barcode.Barcode, new
            {
                barcode = barcode.Barcode,
                productId = barcode.ProductId,
                isPrimary = barcode.IsPrimary,
                isActive = barcode.IsActive,
            }));
        }

        List<PriceBaselineRow> prices = await context.ProductPrices
            .AsNoTracking()
            .Where(p => p.LocationId == null || p.LocationId == device.LocationId)
            .Select(p => new PriceBaselineRow(
                p.Id.Value, p.ProductId.Value, p.LocationId.HasValue ? p.LocationId.Value.Value : (Guid?)null,
                p.Amount, p.Price.Currency, p.EffectiveFromUtc, p.EffectiveToUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (PriceBaselineRow price in prices)
        {
            items.Add(Item("ProductPriceChanged", price.PriceId, new
            {
                priceId = price.PriceId,
                productId = price.ProductId,
                locationId = price.LocationId,
                amount = price.Amount,
                currency = price.Currency,
                effectiveFromUtc = price.EffectiveFromUtc,
                effectiveToUtc = price.EffectiveToUtc,
            }));
        }

        Location? location = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == device.LocationId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (location is not null)
        {
            string currencyCode = await context.Organizations
                .Where(o => o.Id == location.OrganizationId)
                .Select(o => o.CurrencyCode)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) ?? "PHP";
            items.Add(Item("LocationChanged", location.Id.Value, new
            {
                locationId = location.Id.Value,
                code = location.Code,
                name = location.Name,
                kind = location.Kind,
                timeZoneId = location.TimeZoneId,
                currencyCode,
                isActive = location.IsActive,
                settingsJson = location.Settings.ToJson(),
            }));
        }

        await AddStoreStaffAsync(items, currentUser.UserId.Value, device.LocationId, cancellationToken)
            .ConfigureAwait(false);

        long cursor = await context.SyncChangeLog
            .AsNoTracking()
            .Select(e => (long?)e.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new SyncBaselineResponse(clock.UtcNow, cursor, items, null, null);
    }

    /// <summary>
    /// Adds the cached identity and offline authority of everyone who may work
    /// at the register's store: the caller always, and every other active
    /// person assigned to the store or acting business-wide who holds anything
    /// they could do there offline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a register learns what people may do once the connection
    /// drops: offline-capable permissions only, scoped to the register's own
    /// store, and bounded by <see cref="SecurityOptions.PermissionSnapshotHours"/>.
    /// Carrying the whole store's staff rather than the caller alone is what
    /// lets any of them sign in at the register while head office is
    /// unreachable, and it keeps their authority fresh whenever anyone signs in
    /// while connected.
    /// </para>
    /// <para>
    /// A baseline replaces the register's cached people wholesale, so someone
    /// disabled, or moved to another store, is simply absent from the next one —
    /// and a register refuses an offline sign-in for anyone its store data does
    /// not list. Nothing here is a credential: a register can only check a
    /// password it has itself seen head office accept.
    /// </para>
    /// </remarks>
    private async Task AddStoreStaffAsync(
        List<SyncBaselineItem> items,
        UserId callerId,
        LocationId deviceLocation,
        CancellationToken cancellationToken)
    {
        List<UserId> assigned = await context.UserLocations
            .AsNoTracking()
            .Where(a => a.LocationId == deviceLocation)
            .Select(a => a.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<Guid> viaRole = await (
                from userRole in context.UserRoles.AsNoTracking()
                join grant in context.RolePermissions.AsNoTracking() on userRole.RoleId equals grant.RoleId
                where grant.PermissionCode == Permissions.Administration.AllLocations
                select userRole.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<UserId> viaOverride = await context.UserPermissionOverrides
            .AsNoTracking()
            .Where(o => o.PermissionCode == Permissions.Administration.AllLocations
                        && o.Effect == PermissionEffect.Grant)
            .Select(o => o.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The caller first, as before; everyone else in a stable order. Whether
        // each of them may really act here is decided by their resolved
        // authority below, which applies expiry and deny overrides.
        IEnumerable<Guid> candidates = new[] { callerId.Value }
            .Concat(assigned.Select(u => u.Value)
                .Concat(viaRole)
                .Concat(viaOverride.Select(u => u.Value))
                .Distinct()
                .Order());

        foreach (Guid candidate in candidates.Distinct())
        {
            await AddPersonAsync(
                    items,
                    new UserId(candidate),
                    deviceLocation,
                    isCaller: candidate == callerId.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task AddPersonAsync(
        List<SyncBaselineItem> items,
        UserId userId,
        LocationId deviceLocation,
        bool isCaller,
        CancellationToken cancellationToken)
    {
        AppUser? user = await context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        UserAuthorization authority = await authorization
            .GetAuthorizationAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        // Someone not assigned to this store is given nothing to hold here, even
        // if their permissions would allow it somewhere else.
        bool atThisStore = authority.IsActive
            && (authority.HasAllLocations || authority.Locations.Contains(deviceLocation));
        string[] offline = atThisStore
            ? [.. authority.Permissions
                .Where(p => Permissions.Find(p)?.IsOfflineCapable == true)
                .Order(StringComparer.Ordinal)]
            : [];

        // The caller is always described, so the register can show who signed
        // in. Anyone else is carried only if they could do something here.
        if (!isCaller && (!user.CanAuthenticate || offline.Length == 0))
        {
            return;
        }

        items.Add(Item("UserChanged", user.Id, new
        {
            userId = user.Id,
            userName = user.UserName ?? string.Empty,
            displayName = user.DisplayName,
            isActive = user.IsActive,
            securityVersion = 0L,
        }));

        DateTimeOffset issuedAt = clock.UtcNow;
        long version = await policyVersion.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        items.Add(Item("PermissionSnapshotIssued", user.Id, new
        {
            userId = user.Id,
            policyVersion = version,
            issuedAtUtc = issuedAt,
            expiresAtUtc = issuedAt.AddHours(security.Value.PermissionSnapshotHours),
            grants = offline.Select(p => new { permission = p, locationId = (Guid?)deviceLocation.Value }).ToArray(),
        }));
    }

    private static SyncBaselineItem Item<T>(string type, object key, T payload)
        => new(type, key.ToString() ?? string.Empty, JsonSerializer.SerializeToElement(payload));

    private sealed record ProductBaselineRow(Guid ProductId, string Sku, string Name, bool IsActive, bool TracksBatches, bool TracksExpiry, DateTimeOffset UpdatedAtUtc, Guid BaseUnitOfMeasureId);
    private sealed record BarcodeBaselineRow(string Barcode, Guid ProductId, bool IsPrimary, bool IsActive);
    private sealed record PriceBaselineRow(Guid PriceId, Guid ProductId, Guid? LocationId, decimal Amount, string Currency, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc);
}

public sealed record SyncBaselineItem(string Type, string Key, JsonElement Payload);

public sealed record SyncBaselineResponse(
    DateTimeOffset ServerReceivedAtUtc,
    long Cursor,
    IReadOnlyList<SyncBaselineItem> Items,
    string? ErrorCode,
    string? Message)
{
    public static SyncBaselineResponse Refused(string code, string message)
        => new(DateTimeOffset.UtcNow, 0, [], code, message);
}
