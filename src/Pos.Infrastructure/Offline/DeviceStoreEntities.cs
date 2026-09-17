using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Infrastructure.Offline;

/// <summary>The enrolled identity and location of this device database.</summary>
public sealed class DeviceStoreProfile
{
    private DeviceStoreProfile() { ShortCode = string.Empty; }

    /// <summary>Creates the local device profile written after enrolment.</summary>
    public DeviceStoreProfile(
        DeviceId deviceId,
        LocationId locationId,
        string shortCode,
        DateTimeOffset enrolledAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortCode);
        DeviceId = deviceId;
        LocationId = locationId;
        ShortCode = shortCode.Trim().ToUpperInvariant();
        EnrolledAtUtc = enrolledAtUtc;
    }

    public DeviceId DeviceId { get; private init; }
    public LocationId LocationId { get; private set; }
    public string ShortCode { get; private init; }
    public DateTimeOffset EnrolledAtUtc { get; private init; }
}

/// <summary>A product mirror downloaded from the server change feed.</summary>
public sealed class DeviceCachedProduct : IChangeFeedOwned
{
    private DeviceCachedProduct() { Sku = string.Empty; Name = string.Empty; }

    public DeviceCachedProduct(
        ProductId id,
        string sku,
        string name,
        bool isActive,
        bool tracksBatches,
        bool tracksExpiry,
        long sourceVersion,
        DateTimeOffset updatedAtUtc)
    {
        Id = id;
        Sku = sku;
        Name = name;
        IsActive = isActive;
        TracksBatches = tracksBatches;
        TracksExpiry = tracksExpiry;
        SourceVersion = sourceVersion;
        UpdatedAtUtc = updatedAtUtc;
    }

    public ProductId Id { get; private init; }
    public string Sku { get; private set; }
    public string Name { get; private set; }
    public bool IsActive { get; private set; }
    public bool TracksBatches { get; private set; }
    public bool TracksExpiry { get; private set; }
    public long SourceVersion { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    internal void Refresh(
        string sku,
        string name,
        bool isActive,
        bool tracksBatches,
        bool tracksExpiry,
        long sourceVersion,
        DateTimeOffset updatedAtUtc)
    {
        Sku = sku;
        Name = name;
        IsActive = isActive;
        TracksBatches = tracksBatches;
        TracksExpiry = tracksExpiry;
        SourceVersion = sourceVersion;
        UpdatedAtUtc = updatedAtUtc;
    }
}

/// <summary>A barcode mirror used by offline product lookup.</summary>
public sealed class DeviceCachedProductBarcode : IChangeFeedOwned
{
    private DeviceCachedProductBarcode() { Barcode = string.Empty; }

    public DeviceCachedProductBarcode(string barcode, ProductId productId, bool isPrimary, bool isActive)
    {
        Barcode = barcode;
        ProductId = productId;
        IsPrimary = isPrimary;
        IsActive = isActive;
    }

    public string Barcode { get; private init; }
    public ProductId ProductId { get; private set; }
    public bool IsPrimary { get; private set; }
    public bool IsActive { get; private set; }

    internal void Refresh(ProductId productId, bool isPrimary, bool isActive)
    {
        ProductId = productId;
        IsPrimary = isPrimary;
        IsActive = isActive;
    }
}

/// <summary>An effective-dated selling-price mirror for offline resolution.</summary>
public sealed class DeviceCachedProductPrice : IChangeFeedOwned
{
    private DeviceCachedProductPrice() { Currency = string.Empty; }

    public DeviceCachedProductPrice(
        ProductPriceId id,
        ProductId productId,
        LocationId? locationId,
        decimal amount,
        string currency,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc)
    {
        Id = id;
        ProductId = productId;
        LocationId = locationId;
        Amount = amount;
        Currency = currency;
        EffectiveFromUtc = effectiveFromUtc;
        EffectiveToUtc = effectiveToUtc;
    }

    public ProductPriceId Id { get; private init; }
    public ProductId ProductId { get; private set; }
    public LocationId? LocationId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }
    public DateTimeOffset EffectiveFromUtc { get; private set; }
    public DateTimeOffset? EffectiveToUtc { get; private set; }

    internal void Refresh(
        ProductId productId,
        LocationId? locationId,
        decimal amount,
        string currency,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc)
    {
        ProductId = productId;
        LocationId = locationId;
        Amount = amount;
        Currency = currency;
        EffectiveFromUtc = effectiveFromUtc;
        EffectiveToUtc = effectiveToUtc;
    }
}

/// <summary>A location mirror containing only fields needed by the device.</summary>
public sealed class DeviceCachedLocation : IChangeFeedOwned
{
    private DeviceCachedLocation()
    {
        Code = string.Empty;
        Name = string.Empty;
        TimeZoneId = string.Empty;
        CurrencyCode = string.Empty;
    }

    public DeviceCachedLocation(
        LocationId id,
        string code,
        string name,
        LocationKind kind,
        string timeZoneId,
        string currencyCode,
        bool isActive)
    {
        Id = id;
        Code = code;
        Name = name;
        Kind = kind;
        TimeZoneId = timeZoneId;
        CurrencyCode = currencyCode;
        IsActive = isActive;
    }

    public LocationId Id { get; private init; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public LocationKind Kind { get; private set; }
    public string TimeZoneId { get; private set; }
    public string CurrencyCode { get; private set; }
    public bool IsActive { get; private set; }

    internal void Refresh(
        string code,
        string name,
        LocationKind kind,
        string timeZoneId,
        string currencyCode,
        bool isActive)
    {
        Code = code;
        Name = name;
        Kind = kind;
        TimeZoneId = timeZoneId;
        CurrencyCode = currencyCode;
        IsActive = isActive;
    }
}

/// <summary>A minimal user mirror used for offline sign-in and display.</summary>
public sealed class DeviceCachedUser : IChangeFeedOwned
{
    private DeviceCachedUser() { UserName = string.Empty; DisplayName = string.Empty; }

    public DeviceCachedUser(UserId id, string userName, string displayName, bool isActive, long securityVersion)
    {
        Id = id;
        UserName = userName;
        DisplayName = displayName;
        IsActive = isActive;
        SecurityVersion = securityVersion;
    }

    public UserId Id { get; private init; }
    public string UserName { get; private set; }
    public string DisplayName { get; private set; }
    public bool IsActive { get; private set; }
    public long SecurityVersion { get; private set; }

    internal void Refresh(string userName, string displayName, bool isActive, long securityVersion)
    {
        UserName = userName;
        DisplayName = displayName;
        IsActive = isActive;
        SecurityVersion = securityVersion;
    }
}

/// <summary>
/// The position of a downloaded feed. It advances only inside the transaction
/// that applies the page it follows, so an interrupted pull replays harmlessly.
/// </summary>
public sealed class DeviceSyncCursor : IChangeFeedOwned
{
    private DeviceSyncCursor() { Feed = string.Empty; }

    internal DeviceSyncCursor(string feed, long position, DateTimeOffset advancedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feed);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        Feed = feed;
        Position = position;
        AdvancedAtUtc = advancedAtUtc;
    }

    public string Feed { get; private init; }
    public long Position { get; private set; }
    public DateTimeOffset AdvancedAtUtc { get; private set; }

    internal void Advance(long position, DateTimeOffset advancedAtUtc)
    {
        if (position < Position)
        {
            throw new InvalidOperationException("A feed cursor never moves backwards.");
        }

        Position = position;
        AdvancedAtUtc = advancedAtUtc;
    }
}

/// <summary>A time-bounded cached permission; offline checks may only narrow it.</summary>
public sealed class DevicePermissionSnapshot : IChangeFeedOwned
{
    private DevicePermissionSnapshot() { Permission = string.Empty; }

    public DevicePermissionSnapshot(
        Guid id,
        UserId userId,
        string permission,
        LocationId? locationId,
        long policyVersion,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        UserId = userId;
        Permission = permission;
        LocationId = locationId;
        PolicyVersion = policyVersion;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private init; }
    public UserId UserId { get; private init; }
    public string Permission { get; private init; }
    public LocationId? LocationId { get; private init; }
    public long PolicyVersion { get; private init; }
    public DateTimeOffset IssuedAtUtc { get; private init; }
    public DateTimeOffset ExpiresAtUtc { get; private init; }
}
