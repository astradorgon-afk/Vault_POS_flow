using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Devices;
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
        Dictionary<CategoryId, string> categories = await context.Categories
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken)
            .ConfigureAwait(false);
        List<ProductBaselineRow> products = [.. (await context.Products
            .AsNoTracking()
            .Select(p => new { p.Id, Sku = p.Sku.Value, p.Name, p.IsActive, p.TracksBatches, p.TracksExpiry, p.UpdatedAtUtc, p.CategoryId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(p => new ProductBaselineRow(
                p.Id.Value, p.Sku, p.Name, p.IsActive, p.TracksBatches, p.TracksExpiry, p.UpdatedAtUtc,
                categories.GetValueOrDefault(p.CategoryId)))];
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
                category = product.Category,
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

        await AddSignedInUserAsync(items, currentUser.UserId.Value, device.LocationId, cancellationToken)
            .ConfigureAwait(false);

        long cursor = await context.SyncChangeLog
            .AsNoTracking()
            .Select(e => (long?)e.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new SyncBaselineResponse(clock.UtcNow, cursor, items, null, null);
    }

    /// <summary>
    /// Adds the caller's cached identity and offline authority. A register signs
    /// someone in only while it can reach head office, so this is the moment it
    /// learns what they may do once the connection drops: offline-capable
    /// permissions only, scoped to the register's own store, and bounded by
    /// <see cref="SecurityOptions.PermissionSnapshotHours"/>.
    /// </summary>
    private async Task AddSignedInUserAsync(
        List<SyncBaselineItem> items,
        UserId userId,
        LocationId deviceLocation,
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

        items.Add(Item("UserChanged", user.Id, new
        {
            userId = user.Id,
            userName = user.UserName ?? string.Empty,
            displayName = user.DisplayName,
            isActive = user.IsActive,
            securityVersion = 0L,
        }));

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

    private sealed record ProductBaselineRow(Guid ProductId, string Sku, string Name, bool IsActive, bool TracksBatches, bool TracksExpiry, DateTimeOffset UpdatedAtUtc, string? Category);
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
