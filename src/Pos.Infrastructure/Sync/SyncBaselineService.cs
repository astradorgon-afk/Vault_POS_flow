using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Domain.Common;
using Pos.Domain.Devices;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sync;

/// <summary>Builds a device-scoped master-data baseline for re-enrolment.</summary>
public sealed class SyncBaselineService(
    PosDbContext context,
    ICurrentUser currentUser,
    ISystemClock clock)
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
                p.Id.Value, p.Sku.Value, p.Name, p.IsActive, p.TracksBatches, p.TracksExpiry, p.UpdatedAtUtc))
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

        long cursor = await context.SyncChangeLog
            .AsNoTracking()
            .Select(e => (long?)e.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new SyncBaselineResponse(clock.UtcNow, cursor, items, null, null);
    }

    private static SyncBaselineItem Item<T>(string type, object key, T payload)
        => new(type, key.ToString() ?? string.Empty, JsonSerializer.SerializeToElement(payload));

    private sealed record ProductBaselineRow(Guid ProductId, string Sku, string Name, bool IsActive, bool TracksBatches, bool TracksExpiry, DateTimeOffset UpdatedAtUtc);
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
