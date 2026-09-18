using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Reads change-feed entries as head office sends them — a type name and a JSON
/// payload — into the typed changes <see cref="ChangeFeedApplier"/> writes.
/// </summary>
/// <remarks>
/// Reading is all this does. Whether a change is acceptable is decided by the
/// applier's validation, so a payload that parses but carries a blank name or a
/// permission a device may not hold is refused there, whole, like any other page.
/// </remarks>
public static class ChangeFeedWire
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads a baseline download into a replacement the applier can install.</summary>
    /// <param name="cursor">The feed position the baseline was taken at.</param>
    /// <param name="items">The baseline's entries, in the order they were sent.</param>
    /// <returns>The baseline, or <c>sync.feed_page_invalid</c> for an entry that cannot be read.</returns>
    public static Result<ChangeFeedBaseline> ReadBaseline(
        long cursor,
        IEnumerable<(string Type, JsonElement Payload)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<ChangeFeedChange> changes = [];
        foreach ((string type, JsonElement payload) in items)
        {
            // Baseline entries carry no feed sequence; the cursor is the position.
            Result<ChangeFeedChange> change = Read(0, type, payload);
            if (change.IsFailure)
            {
                return Result.Failure<ChangeFeedBaseline>(change.Error);
            }

            changes.Add(change.Value);
        }

        return Result.Success(new ChangeFeedBaseline(cursor, changes));
    }

    /// <summary>Reads one entry.</summary>
    /// <param name="sequence">The entry's feed sequence, or zero in a baseline.</param>
    /// <param name="type">The change type name.</param>
    /// <param name="payload">The change payload.</param>
    /// <returns>The typed change, or <c>sync.feed_page_invalid</c>.</returns>
    public static Result<ChangeFeedChange> Read(long sequence, string type, JsonElement payload)
    {
        try
        {
            ChangeFeedChange? change = type switch
            {
                "ProductChanged" => Parse<ProductWire>(payload) is { } p
                    ? new ProductChanged(
                        sequence, new ProductId(p.ProductId), p.Sku, p.Name, p.IsActive,
                        p.TracksBatches, p.TracksExpiry, p.SourceVersion, p.UpdatedAtUtc)
                    : null,
                "ProductBarcodeChanged" => Parse<BarcodeWire>(payload) is { } b
                    ? new ProductBarcodeChanged(sequence, b.Barcode, new ProductId(b.ProductId), b.IsPrimary, b.IsActive)
                    : null,
                "ProductPriceChanged" => Parse<PriceWire>(payload) is { } p
                    ? new ProductPriceChanged(
                        sequence, new ProductPriceId(p.PriceId), new ProductId(p.ProductId),
                        p.LocationId is { } location ? new LocationId(location) : null,
                        p.Amount, p.Currency, p.EffectiveFromUtc, p.EffectiveToUtc)
                    : null,
                "ProductPriceRemoved" => Parse<PriceRemovedWire>(payload) is { } r
                    ? new ProductPriceRemoved(sequence, new ProductPriceId(r.PriceId))
                    : null,
                "LocationChanged" => Parse<LocationWire>(payload) is { } l
                    ? new LocationChanged(
                        sequence, new LocationId(l.LocationId), l.Code, l.Name, l.Kind,
                        l.TimeZoneId, l.CurrencyCode, l.IsActive, l.SettingsJson)
                    : null,
                "UserChanged" => Parse<UserWire>(payload) is { } u
                    ? new UserChanged(sequence, new UserId(u.UserId), u.UserName, u.DisplayName, u.IsActive, u.SecurityVersion)
                    : null,
                "PermissionSnapshotIssued" => Parse<SnapshotWire>(payload) is { } s
                    ? new PermissionSnapshotIssued(
                        sequence, new UserId(s.UserId), s.PolicyVersion, s.IssuedAtUtc, s.ExpiresAtUtc,
                        [.. (s.Grants ?? []).Select(g => new PermissionSnapshotGrant(
                            g.Permission, g.LocationId is { } location ? new LocationId(location) : null))])
                    : null,
                "PermissionSnapshotRevoked" => Parse<SnapshotRevokedWire>(payload) is { } r
                    ? new PermissionSnapshotRevoked(sequence, new UserId(r.UserId))
                    : null,
                _ => null,
            };

            return change is null
                ? Result.Failure<ChangeFeedChange>(ChangeFeedErrors.PageInvalid(
                    "The change type " + type + " is not supported by this client.", sequence))
                : Result.Success(change);
        }
        catch (JsonException)
        {
            return Result.Failure<ChangeFeedChange>(ChangeFeedErrors.PageInvalid(
                "A " + type + " payload could not be read.", sequence));
        }
    }

    private static T? Parse<T>(JsonElement payload)
        where T : class
        => payload.ValueKind == JsonValueKind.Object ? payload.Deserialize<T>(Options) : null;

    private sealed record ProductWire(
        Guid ProductId, string Sku, string Name, bool IsActive, bool TracksBatches, bool TracksExpiry,
        long SourceVersion, DateTimeOffset UpdatedAtUtc);

    private sealed record BarcodeWire(string Barcode, Guid ProductId, bool IsPrimary, bool IsActive);

    private sealed record PriceWire(
        Guid PriceId, Guid ProductId, Guid? LocationId, decimal Amount, string Currency,
        DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc);

    private sealed record PriceRemovedWire(Guid PriceId);

    private sealed record LocationWire(
        Guid LocationId, string Code, string Name, LocationKind Kind, string TimeZoneId,
        string CurrencyCode, bool IsActive, string? SettingsJson);

    private sealed record UserWire(Guid UserId, string UserName, string DisplayName, bool IsActive, long SecurityVersion);

    private sealed record SnapshotWire(
        Guid UserId, long PolicyVersion, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<GrantWire>? Grants);

    private sealed record GrantWire(string Permission, Guid? LocationId);

    private sealed record SnapshotRevokedWire(Guid UserId);
}
