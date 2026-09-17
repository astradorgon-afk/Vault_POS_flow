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

/// <summary>
/// The device's own document-number counter. It is the one authoritative table
/// on a device that the change feed never touches: SAL, RET and SHF numbers are
/// allocated here so a sale rung up offline keeps the number printed on its
/// receipt (POS.md §11, ADR-0008). The server verifies the device code on a
/// posted number; it never allocates one.
/// </summary>
public sealed class DeviceDocumentCounter
{
    private DeviceDocumentCounter() { PeriodKey = string.Empty; }

    /// <summary>Creates a counter for one document type and period.</summary>
    /// <param name="documentType">The device-scoped document type.</param>
    /// <param name="periodKey">The four-digit year the sequence restarts on.</param>
    /// <param name="nextValue">The next value to hand out.</param>
    internal DeviceDocumentCounter(DocumentType documentType, string periodKey, long nextValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextValue);
        DocumentType = documentType;
        PeriodKey = periodKey;
        NextValue = nextValue;
    }

    /// <summary>Gets the document type this counter numbers.</summary>
    public DocumentType DocumentType { get; private init; }

    /// <summary>Gets the period the sequence belongs to, as a four-digit year.</summary>
    public string PeriodKey { get; private init; }

    /// <summary>Gets the next sequence value this counter will hand out.</summary>
    public long NextValue { get; private set; }
}

/// <summary>
/// The device's outbox sequence. One row, incremented in the same transaction as
/// the event it numbers, which is what makes the per-device order gapless.
/// </summary>
public sealed class DeviceSequence
{
    private DeviceSequence() { Name = string.Empty; }

    /// <summary>Creates a named counter.</summary>
    /// <param name="name">The counter name.</param>
    /// <param name="nextValue">The next value to hand out.</param>
    internal DeviceSequence(string name, long nextValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        NextValue = nextValue;
    }

    /// <summary>Gets the counter name.</summary>
    public string Name { get; private init; }

    /// <summary>Gets the next value this counter will hand out.</summary>
    public long NextValue { get; private set; }
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
        DateTimeOffset updatedAtUtc,
        bool isVatExempt = false)
    {
        Id = id;
        Sku = sku;
        Name = name;
        IsActive = isActive;
        TracksBatches = tracksBatches;
        TracksExpiry = tracksExpiry;
        SourceVersion = sourceVersion;
        UpdatedAtUtc = updatedAtUtc;
        IsVatExempt = isVatExempt;
    }

    public ProductId Id { get; private init; }
    public string Sku { get; private set; }
    public string Name { get; private set; }
    public bool IsActive { get; private set; }
    public bool TracksBatches { get; private set; }
    public bool TracksExpiry { get; private set; }
    public long SourceVersion { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Gets whether the product is exempt from VAT. Cached because an offline
    /// sale computes its own tax: getting this wrong would put the wrong figure
    /// on a receipt a customer keeps.
    /// </summary>
    public bool IsVatExempt { get; private set; }

    internal void Refresh(
        string sku,
        string name,
        bool isActive,
        bool tracksBatches,
        bool tracksExpiry,
        long sourceVersion,
        DateTimeOffset updatedAtUtc,
        bool isVatExempt)
    {
        Sku = sku;
        Name = name;
        IsActive = isActive;
        TracksBatches = tracksBatches;
        TracksExpiry = tracksExpiry;
        SourceVersion = sourceVersion;
        UpdatedAtUtc = updatedAtUtc;
        IsVatExempt = isVatExempt;
    }
}

/// <summary>
/// A batch mirror. A device that sells batch-tracked stock has to allocate it
/// first-expiry-first-out and refuse what has expired, and both are decisions it
/// cannot make without knowing each batch's expiry date and cost.
/// </summary>
public sealed class DeviceCachedBatch : IChangeFeedOwned
{
    private DeviceCachedBatch() { LotNumber = string.Empty; }

    /// <summary>Creates a cached batch.</summary>
    public DeviceCachedBatch(
        BatchId id,
        ProductId productId,
        string lotNumber,
        DateOnly receivedOn,
        DateOnly? expiresOn,
        decimal unitCost)
    {
        Id = id;
        ProductId = productId;
        LotNumber = lotNumber;
        ReceivedOn = receivedOn;
        ExpiresOn = expiresOn;
        UnitCost = unitCost;
    }

    public BatchId Id { get; private init; }
    public ProductId ProductId { get; private set; }
    public string LotNumber { get; private set; }
    public DateOnly ReceivedOn { get; private set; }

    /// <summary>Gets when the batch expires, or null when it does not.</summary>
    public DateOnly? ExpiresOn { get; private set; }

    public decimal UnitCost { get; private set; }

    internal void Refresh(
        ProductId productId,
        string lotNumber,
        DateOnly receivedOn,
        DateOnly? expiresOn,
        decimal unitCost)
    {
        ProductId = productId;
        LotNumber = lotNumber;
        ReceivedOn = receivedOn;
        ExpiresOn = expiresOn;
        UnitCost = unitCost;
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
        bool isActive,
        string? settingsJson = null)
    {
        Id = id;
        Code = code;
        Name = name;
        Kind = kind;
        TimeZoneId = timeZoneId;
        CurrencyCode = currencyCode;
        IsActive = isActive;
        SettingsJson = settingsJson;
    }

    public LocationId Id { get; private init; }
    public string Code { get; private set; }
    public string Name { get; private set; }
    public LocationKind Kind { get; private set; }
    public string TimeZoneId { get; private set; }
    public string CurrencyCode { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>
    /// Gets this location's operational settings as stored JSON, or
    /// <see langword="null"/> when the feed has not carried them. Null means the
    /// strictest configuration, never a permissive guess
    /// (<see cref="Pos.Domain.Organizations.LocationSettings.FromJson"/>).
    /// </summary>
    public string? SettingsJson { get; private set; }

    internal void Refresh(
        string code,
        string name,
        LocationKind kind,
        string timeZoneId,
        string currencyCode,
        bool isActive,
        string? settingsJson)
    {
        Code = code;
        Name = name;
        Kind = kind;
        TimeZoneId = timeZoneId;
        CurrencyCode = currencyCode;
        IsActive = isActive;
        SettingsJson = settingsJson;
    }
}

/// <summary>
/// An audit entry written on the device. The device is the only witness to what
/// happened on it while it was offline, so entries are appended here and travel
/// with the events they describe; nothing rewrites or deletes one.
/// </summary>
public sealed class DeviceLocalAudit
{
    private DeviceLocalAudit() { Action = string.Empty; EntityType = string.Empty; }

    /// <summary>Records one audited action.</summary>
    public DeviceLocalAudit(
        Guid id,
        string action,
        string entityType,
        Guid? entityId,
        UserId? userId,
        DeviceId? deviceId,
        LocationId? locationId,
        string? previousValueJson,
        string? newValueJson,
        string? reason,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        Id = id;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        UserId = userId;
        DeviceId = deviceId;
        LocationId = locationId;
        PreviousValueJson = previousValueJson;
        NewValueJson = newValueJson;
        Reason = reason;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; private init; }
    public string Action { get; private init; }
    public string EntityType { get; private init; }
    public Guid? EntityId { get; private init; }
    public UserId? UserId { get; private init; }
    public DeviceId? DeviceId { get; private init; }
    public LocationId? LocationId { get; private init; }
    public string? PreviousValueJson { get; private init; }
    public string? NewValueJson { get; private init; }
    public string? Reason { get; private init; }
    public DateTimeOffset RecordedAtUtc { get; private init; }
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
