using Pos.Domain.Common;

namespace Pos.Domain.Organizations;

/// <summary>
/// The business this installation manages. A single-business deployment has
/// exactly one active organization; the row exists to bind locations,
/// currency and the default timezone in one place, and to leave the door open
/// to future segmentation without redesigning every foreign key.
/// </summary>
public sealed class Organization : AggregateRoot<OrganizationId>
{
    /// <summary>
    /// The identifier of the single organization seeded on an empty system. Fixed
    /// so bulk operations (and the development seeder) can reference the
    /// business without a lookup round-trip.
    /// </summary>
    public static OrganizationId DefaultId { get; } = new(new Guid("11111111-1111-7111-8111-111111111111"));

    /// <summary>Maximum length of an organization name.</summary>
    public const int NameMaxLength = 128;
    private Organization(
        OrganizationId id,
        string name,
        string legalName,
        string currencyCode,
        string defaultTimeZoneId,
        string? taxSettingsJson)
    {
        Id = id;
        Name = name;
        LegalName = legalName;
        CurrencyCode = currencyCode;
        DefaultTimeZoneId = defaultTimeZoneId;
        TaxSettingsJson = taxSettingsJson;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Required by the persistence provider for materialization.</summary>
    private Organization()
    {
        Name = string.Empty;
        LegalName = string.Empty;
        CurrencyCode = string.Empty;
        DefaultTimeZoneId = string.Empty;
    }

    /// <summary>Gets the trading name.</summary>
    public string Name { get; private set; }

    /// <summary>Gets the legal or registered name.</summary>
    public string LegalName { get; private set; }

    /// <summary>Gets the ISO-4217 currency code every financial row inherits.</summary>
    public string CurrencyCode { get; private set; }

    /// <summary>
    /// Gets the IANA timezone identifier used to compute business dates for
    /// locations that do not override it.
    /// </summary>
    public string DefaultTimeZoneId { get; private set; }

    /// <summary>Gets serialized VAT / tax configuration, if any.</summary>
    public string? TaxSettingsJson { get; private set; }

    /// <summary>Gets when the organization was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Creates a new organization.</summary>
    /// <param name="name">The trading name.</param>
    /// <param name="legalName">The legal or registered name.</param>
    /// <param name="currencyCode">ISO-4217 alphabetic currency code.</param>
    /// <param name="defaultTimeZoneId">IANA timezone identifier.</param>
    /// <param name="taxSettingsJson">Serialized tax settings, or <see langword="null"/>.</param>
    /// <returns>The new organization, or a validation failure.</returns>
    public static Result<Organization> Create(
        string name,
        string legalName,
        string currencyCode,
        string defaultTimeZoneId,
        string? taxSettingsJson = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<Organization>.Failure(
                Error.Validation("organization.name_required", "An organization name is required."));
        }

        if (string.IsNullOrWhiteSpace(defaultTimeZoneId))
        {
            return Result<Organization>.Failure(
                Error.Validation("organization.timezone_required", "A default timezone is required."));
        }

        if (string.IsNullOrWhiteSpace(currencyCode)
            || currencyCode.Trim().Length != 3
            || !currencyCode.Trim().All(char.IsAsciiLetter))
        {
            return Result<Organization>.Failure(Error.Validation(
                "organization.invalid_currency",
                "The organization currency must be a three-letter ISO-4217 code."));
        }

        Organization organization = new(
            OrganizationId.New(),
            name.Trim(),
            string.IsNullOrWhiteSpace(legalName) ? name.Trim() : legalName.Trim(),
            currencyCode.Trim().ToUpperInvariant(),
            defaultTimeZoneId.Trim(),
            taxSettingsJson);

        return Result<Organization>.Success(organization);
    }
}