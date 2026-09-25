using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Checks a downloaded page before any of it is written. The feed arrives over
/// the network, so a malformed page is refused whole rather than half-applied or
/// silently coerced by a column converter.
/// </summary>
internal static class ChangeFeedPageValidator
{
    /// <summary>Validates cursor order, change order and every change's fields.</summary>
    public static Result Validate(ChangeFeedPage page)
    {
        if (page.Changes is null)
        {
            return Invalid("The page carries no change list.");
        }

        if (page.FromCursor < 0)
        {
            return Invalid("The page cursor cannot be negative.");
        }

        if (page.NextCursor < page.FromCursor)
        {
            return Invalid("The next cursor cannot precede the page cursor.");
        }

        long previous = page.FromCursor;
        foreach (ChangeFeedChange? change in page.Changes)
        {
            if (change is null)
            {
                return Invalid("The page contains an empty change.");
            }

            if (change.Sequence <= previous)
            {
                return Invalid("Changes must follow the page cursor in ascending sequence order.", change.Sequence);
            }

            if (change.Sequence > page.NextCursor)
            {
                return Invalid("A change lies beyond the page's next cursor.", change.Sequence);
            }

            previous = change.Sequence;
            string? problem = Check(change);
            if (problem is not null)
            {
                return Invalid(problem, change.Sequence);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Validates every change in a baseline. A baseline replaces the caches
    /// wholesale and now carries permission snapshots, so it is held to the same
    /// field rules as a page; only the sequence rules do not apply, because its
    /// changes carry none.
    /// </summary>
    public static Result Validate(ChangeFeedBaseline baseline)
    {
        if (baseline.Changes is null)
        {
            return Invalid("The baseline carries no change list.");
        }

        foreach (ChangeFeedChange? change in baseline.Changes)
        {
            string? problem = change is null ? "The baseline contains an empty change." : Check(change);
            if (problem is not null)
            {
                return Invalid(problem);
            }
        }

        return Result.Success();
    }

    private static string? Check(ChangeFeedChange change) => change switch
    {
        ProductChanged p => Id(p.ProductId, "product")
            ?? Text(p.Sku, 64, "SKU")
            ?? Text(p.Name, 240, "product name")
            ?? (p.SourceVersion < 0 ? "A product source version cannot be negative." : null)
            ?? OptionalId(p.BaseUnitOfMeasureId, "base unit"),
        ProductBarcodeChanged b => Text(b.Barcode, 64, "barcode") ?? Id(b.ProductId, "product"),
        ProductPriceChanged p => Id(p.PriceId, "price")
            ?? Id(p.ProductId, "product")
            ?? OptionalId(p.LocationId, "location")
            ?? Amount(p.Amount)
            ?? Currency(p.Currency)
            ?? (p.EffectiveToUtc is { } to && to <= p.EffectiveFromUtc
                ? "A price period must end after it starts."
                : null),
        ProductPriceRemoved r => Id(r.PriceId, "price"),
        LocationChanged l => Id(l.LocationId, "location")
            ?? Text(l.Code, 32, "location code")
            ?? Text(l.Name, 160, "location name")
            ?? (Enum.IsDefined(l.Kind) ? null : "The location kind is not recognised.")
            ?? Text(l.TimeZoneId, 128, "time zone")
            ?? Currency(l.CurrencyCode)
            ?? (l.SettingsJson is { Length: > 4000 } ? "The location settings are too long." : null),
        UserChanged u => Id(u.UserId, "user")
            ?? Text(u.UserName, 128, "user name")
            ?? Text(u.DisplayName, 160, "display name")
            ?? (u.SecurityVersion < 0 ? "A security version cannot be negative." : null),
        PermissionSnapshotIssued s => Snapshot(s),
        PermissionSnapshotRevoked r => Id(r.UserId, "user"),
        _ => "The change kind is not supported by this client.",
    };

    private static string? Snapshot(PermissionSnapshotIssued snapshot)
    {
        string? problem = Id(snapshot.UserId, "user")
            ?? (snapshot.PolicyVersion < 0 ? "A policy version cannot be negative." : null)
            ?? (snapshot.ExpiresAtUtc <= snapshot.IssuedAtUtc ? "A permission snapshot must expire after it is issued." : null)
            ?? (snapshot.Grants is null ? "A permission snapshot carries no grant list." : null);
        if (problem is not null)
        {
            return problem;
        }

        HashSet<(string Permission, LocationId? LocationId)> seen = [];
        foreach (PermissionSnapshotGrant? grant in snapshot.Grants!)
        {
            problem = grant is null
                ? "A permission snapshot contains an empty grant."
                : Text(grant.Permission, 160, "permission") ?? OptionalId(grant.LocationId, "grant location");
            if (problem is not null)
            {
                return problem;
            }

            if (!seen.Add((grant!.Permission, grant.LocationId)))
            {
                return "A permission snapshot repeats a grant.";
            }

            // The server already trims a device's snapshot to offline-capable
            // permissions. Checking it again here means a feed that widens one —
            // tampered with, or served by a server running an older catalogue —
            // is refused whole rather than stored and relied upon.
            PermissionDefinition? permission = Permissions.Find(grant.Permission);

            if (permission is null)
            {
                return "A permission snapshot names a permission this client does not know.";
            }

            if (!permission.IsOfflineCapable)
            {
                return "A permission snapshot carries a permission a device may not hold offline.";
            }
        }

        return null;
    }

    private static string? Id<TId>(TId id, string name)
        where TId : IStronglyTypedId
        => id.Value == Guid.Empty ? "The " + name + " identifier is empty." : null;

    private static string? OptionalId<TId>(TId? id, string name)
        where TId : struct, IStronglyTypedId
        => id is { } value ? Id(value, name) : null;

    private static string? Text(string? value, int maxLength, string name)
        => string.IsNullOrWhiteSpace(value) ? "The " + name + " is required."
            : value.Length > maxLength ? "The " + name + " is too long."
            : null;

    private static string? Amount(decimal amount)
        => amount < 0 ? "A price cannot be negative."
            : decimal.Round(amount, Money.StorageScale) != amount ? "A price has more decimal places than can be stored exactly."
            : null;

    private static string? Currency(string? code)
        => code is { Length: 3 } && code.All(char.IsAsciiLetterUpper) ? null : "A currency must be a three-letter ISO code.";

    private static Result Invalid(string reason, long? sequence = null)
        => Result.Failure(ChangeFeedErrors.PageInvalid(reason, sequence));
}
