using Pos.Domain.Common;
using Pos.Domain.Locations;

namespace Pos.Domain.Organizations;

/// <summary>
/// The settlement a business operates or trades against. The Main Warehouse,
/// stores and external counterparties are all locations; a location's kind and
/// settings determine what its users and devices may do.
/// </summary>
/// <remarks>
/// <para>
/// External counterparty locations (supplier, customer, write-off) are created
/// by the system and must never appear in a user-facing picker. Every location
/// either holds physical stock (MainWarehouse, Store) or is a virtual ledger
/// counterparty (External).
/// </para>
/// <para>
/// Locations are never deleted. Closing sets <see cref="IsActive"/> to false and
/// is only valid once every balance is zero and no documents are open.
/// </para>
/// </remarks>
public sealed class Location : AggregateRoot<LocationId>
{
    /// <summary>Maximum length of a location code.</summary>
    public const int CodeMaxLength = 16;

    /// <summary>Maximum length of a location name.</summary>
    public const int NameMaxLength = 64;

    private Location(
        LocationId id,
        OrganizationId organizationId,
        string code,
        string name,
        LocationKind kind,
        string timeZoneId,
        LocationSettings settings)
    {
        Id = id;
        OrganizationId = organizationId;
        Code = code;
        Name = name;
        Kind = kind;
        TimeZoneId = timeZoneId;
        Settings = settings;
        IsSystemCreated = kind == LocationKind.External;
        IsActive = true;
        OpenedOn = DateOnly.FromDateTime(DateTime.UtcNow);
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Location()
    {
        Code = string.Empty;
        Name = string.Empty;
        TimeZoneId = string.Empty;
        Settings = LocationSettings.Default;
    }

    /// <summary>Gets the owning organization.</summary>
    public OrganizationId OrganizationId { get; private set; }

    /// <summary>
    /// Gets the short unique code by which the system and reports refer to the
    /// location, for example <c>MAIN</c>, <c>STORE2</c> or <c>EXT-SUPPLIER</c>.
    /// </summary>
    public string Code { get; private set; }

    /// <summary>Gets the human-readable name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets the kind of location.</summary>
    public LocationKind Kind { get; private set; }

    /// <summary>Gets the IANA timezone used to compute the business date here.</summary>
    public string TimeZoneId { get; private set; }

    /// <summary>Gets whether this location is accepting operations.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Gets whether the system created this location (the three external counterparts).</summary>
    public bool IsSystemCreated { get; private set; }

    /// <summary>Gets the first business day of this location.</summary>
    public DateOnly OpenedOn { get; private set; }

    /// <summary>Gets the last business day, when the location is closed.</summary>
    public DateOnly? ClosedOn { get; private set; }

    /// <summary>Gets when the location was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the operational settings, including the negative-stock policy.</summary>
    public LocationSettings Settings { get; private set; }

    /// <summary>Gets a value indicating whether this location holds physical stock.</summary>
    public bool IsPhysical => Kind != LocationKind.External;

    /// <summary>
    /// Creates a location. External system locations cannot be created here;
    /// use <see cref="CreateSystemExternal"/> for those.
    /// </summary>
    /// <param name="organizationId">The owning organization.</param>
    /// <param name="code">The short unique code.</param>
    /// <param name="name">The human-readable name.</param>
    /// <param name="kind">The kind; not <see cref="LocationKind.External"/>.</param>
    /// <param name="timeZoneId">IANA timezone identifier.</param>
    /// <param name="settings">Operational settings; defaults to the strictest.</param>
    /// <returns>The new location, or a validation failure.</returns>
    public static Result<Location> Create(
        OrganizationId organizationId,
        string? code,
        string? name,
        LocationKind kind,
        string? timeZoneId,
        LocationSettings? settings = null)
    {
        if (kind == LocationKind.External)
        {
            return Result<Location>.Failure(Error.Validation(
                "location.external_not_user_creatable",
                "External locations are created by the system, not by users."));
        }

        return CreateInternal(organizationId, code, name, kind, timeZoneId, settings);
    }

    /// <summary>
    /// Creates a system-managed external counterparty location, exactly matching
    /// one of the codes in <see cref="SystemLocationCodes"/>.
    /// </summary>
    /// <param name="organizationId">The owning organization.</param>
    /// <param name="code">The counterparty code.</param>
    /// <param name="name">The display name.</param>
    /// <returns>The external location, or a validation failure.</returns>
    public static Result<Location> CreateSystemExternal(OrganizationId organizationId, string? code, string? name)
    {
        if (string.IsNullOrWhiteSpace(code) || !SystemLocationCodes.All.Contains(code.Trim().ToUpperInvariant()))
        {
            return Result<Location>.Failure(Error.Validation(
                "location.unknown_external_code",
                "An external location must use one of the well-known counterparty codes."));
        }

        return CreateInternal(
            organizationId,
            code,
            name,
            LocationKind.External,
            "Etc/UTC",
            LocationSettings.Default);
    }

    /// <summary>Closes a location, refusing when balances or documents are open.</summary>
    /// <param name="closingDate">The effective closing business date.</param>
    /// <returns>A success result, or a failure when work is still open.</returns>
    public Result Close(DateOnly closingDate)
    {
        if (IsActive == false)
        {
            return Result.Failure(LocationErrors.AlreadyClosed);
        }

        if (IsSystemCreated)
        {
            return Result.Failure(LocationErrors.CannotCloseSystemLocation);
        }

        IsActive = false;
        ClosedOn = closingDate;
        return Result.Success();
    }

    private static Result<Location> CreateInternal(
        OrganizationId organizationId,
        string? code,
        string? name,
        LocationKind kind,
        string? timeZoneId,
        LocationSettings? settings)
    {
        if (organizationId.IsEmpty)
        {
            return Result<Location>.Failure(
                Error.Validation("location.organization_required", "A location must belong to an organization."));
        }

        string normalisedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;

        if (normalisedCode.Length == 0 || normalisedCode.Length > CodeMaxLength)
        {
            return Result<Location>.Failure(Error.Validation(
                "location.code_invalid",
                FormattableString.Invariant(
                    $"A location code is required and may be at most {CodeMaxLength} characters.")));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            return Result<Location>.Failure(Error.Validation(
                "location.name_invalid",
                FormattableString.Invariant(
                    $"A location name is required and may be at most {NameMaxLength} characters.")));
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return Result<Location>.Failure(
                Error.Validation("location.timezone_required", "A location must specify a timezone."));
        }

        Location location = new(
            LocationId.New(),
            organizationId,
            normalisedCode,
            name.Trim(),
            kind,
            timeZoneId.Trim(),
            settings ?? LocationSettings.Default);

        return Result<Location>.Success(location);
    }
}