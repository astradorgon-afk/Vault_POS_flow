using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Infrastructure.Offline;

/// <summary>One page of the server change feed, requested from a cursor.</summary>
/// <param name="FromCursor">The cursor the page was requested from.</param>
/// <param name="NextCursor">
/// The cursor to store once the page is applied. It may exceed the last change's
/// sequence when the server skipped changes outside the device's scope.
/// </param>
/// <param name="Changes">The changes after <paramref name="FromCursor"/>, in feed order.</param>
public sealed record ChangeFeedPage(long FromCursor, long NextCursor, IReadOnlyList<ChangeFeedChange> Changes);

/// <summary>A single change in the feed.</summary>
/// <param name="Sequence">The server change sequence; strictly ascending within the feed.</param>
public abstract record ChangeFeedChange(long Sequence);

/// <summary>A product was created or changed.</summary>
public sealed record ProductChanged(
    long Sequence,
    ProductId ProductId,
    string Sku,
    string Name,
    bool IsActive,
    bool TracksBatches,
    bool TracksExpiry,
    long SourceVersion,
    DateTimeOffset UpdatedAtUtc) : ChangeFeedChange(Sequence);

/// <summary>A barcode was attached, retired or made primary.</summary>
public sealed record ProductBarcodeChanged(
    long Sequence,
    string Barcode,
    ProductId ProductId,
    bool IsPrimary,
    bool IsActive) : ChangeFeedChange(Sequence);

/// <summary>A selling price was scheduled or its period changed.</summary>
public sealed record ProductPriceChanged(
    long Sequence,
    ProductPriceId PriceId,
    ProductId ProductId,
    LocationId? LocationId,
    decimal Amount,
    string Currency,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc) : ChangeFeedChange(Sequence);

/// <summary>A not-yet-effective price was cancelled and no longer exists.</summary>
public sealed record ProductPriceRemoved(long Sequence, ProductPriceId PriceId) : ChangeFeedChange(Sequence);

/// <summary>A location was created or changed.</summary>
/// <remarks>
/// <paramref name="SettingsJson"/> carries the location's operational settings —
/// the VAT rate, cash rounding, the negative-stock policy and the shift caps —
/// because a device applies its owner's configuration offline, not a guess.
/// Null means the feed did not carry them, which
/// <see cref="Pos.Domain.Organizations.LocationSettings.FromJson"/> reads as the
/// strictest configuration rather than a permissive default.
/// </remarks>
public sealed record LocationChanged(
    long Sequence,
    LocationId LocationId,
    string Code,
    string Name,
    LocationKind Kind,
    string TimeZoneId,
    string CurrencyCode,
    bool IsActive,
    string? SettingsJson = null) : ChangeFeedChange(Sequence);

/// <summary>A user the device may sign in was created or changed.</summary>
public sealed record UserChanged(
    long Sequence,
    UserId UserId,
    string UserName,
    string DisplayName,
    bool IsActive,
    long SecurityVersion) : ChangeFeedChange(Sequence);

/// <summary>A user's offline permission snapshot was issued; it replaces any previous one.</summary>
public sealed record PermissionSnapshotIssued(
    long Sequence,
    UserId UserId,
    long PolicyVersion,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<PermissionSnapshotGrant> Grants) : ChangeFeedChange(Sequence);

/// <summary>One permission in a snapshot, global when <paramref name="LocationId"/> is null.</summary>
public sealed record PermissionSnapshotGrant(string Permission, LocationId? LocationId);

/// <summary>A user's offline permission snapshot was invalidated.</summary>
public sealed record PermissionSnapshotRevoked(long Sequence, UserId UserId) : ChangeFeedChange(Sequence);

/// <summary>The result of applying a page.</summary>
/// <param name="Cursor">The stored cursor after the call.</param>
/// <param name="AppliedChanges">How many changes were written.</param>
/// <param name="AlreadyApplied">True when the page had been committed before and nothing was written.</param>
public sealed record ChangeFeedApplyOutcome(long Cursor, int AppliedChanges, bool AlreadyApplied);

/// <summary>The expected failures of applying a change-feed page.</summary>
public static class ChangeFeedErrors
{
    /// <summary>The page does not start at the stored cursor and was not applied before.</summary>
    public static Error CursorMismatch(long storedCursor, long fromCursor) => Error.Conflict(
        "sync.feed_cursor_mismatch",
        "The change-feed page does not continue from this device's cursor.",
        new Dictionary<string, object?>
        {
            ["storedCursor"] = storedCursor,
            ["fromCursor"] = fromCursor,
        });

    /// <summary>
    /// An issued snapshot is older than the one already stored for that user.
    /// Policy versions only ever increase, so this is a replayed or forged page
    /// trying to restore authority the server has since narrowed.
    /// </summary>
    public static Error SnapshotRollback(long storedVersion, long offeredVersion, long? sequence = null) => Error.Conflict(
        "sync.snapshot_policy_rollback",
        "The permission snapshot is older than the one this device already holds.",
        new Dictionary<string, object?>
        {
            ["storedPolicyVersion"] = storedVersion,
            ["offeredPolicyVersion"] = offeredVersion,
            ["sequence"] = sequence,
        });

    /// <summary>The page is malformed and nothing in it was applied.</summary>
    public static Error PageInvalid(string reason, long? sequence = null) => Error.Validation(
        "sync.feed_page_invalid",
        reason,
        new Dictionary<string, object?> { ["sequence"] = sequence });
}
