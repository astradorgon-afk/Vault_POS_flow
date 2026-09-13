using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Quarantine;

namespace Pos.Application.Quarantine;

/// <summary>
/// Persists quarantine incidents. Mutations opt into tracking explicitly because
/// the context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
public interface IQuarantineRepository
{
    /// <summary>Stages a new incident on the context.</summary>
    /// <param name="incident">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier.</returns>
    Task<Result<QuarantineIncidentId>> AddAsync(
        QuarantineIncident incident,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads an incident with its lines, photographs and timeline, tracked.
    /// </summary>
    /// <param name="incidentId">The incident.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The incident, or <see langword="null"/> when it does not exist.</returns>
    Task<QuarantineIncident?> GetByIdAsync(
        QuarantineIncidentId incidentId,
        CancellationToken cancellationToken);

    /// <summary>Loads a location's kind and timezone for ledger posting.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The location info, or <see langword="null"/> when the location does not exist.</returns>
    Task<QuarantineLocationInfo?> GetLocationInfoAsync(
        LocationId locationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads the products reached through the given barcodes: one product per
    /// barcode value when the barcode is attached in the catalogue and not retired.
    /// </summary>
    /// <param name="barcodes">The scanned barcode values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The barcode-to-product resolution, keyed by normalised barcode value.</returns>
    Task<IReadOnlyDictionary<string, Product>> GetProductsByBarcodesAsync(
        IReadOnlyCollection<string> barcodes,
        CancellationToken cancellationToken);

    /// <summary>Loads the products named on dispositions, untracked.</summary>
    /// <param name="productIds">The product identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The products.</returns>
    Task<IReadOnlyList<Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken);

    /// <summary>Loads the system-created external counterparty with the given code.</summary>
    /// <param name="systemCode">The system location code, for example <c>EXT-SUPPLIER</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counterparty location identifier, or <see langword="null"/> when not provisioned.</returns>
    Task<LocationId?> GetExternalLocationIdAsync(
        string systemCode,
        CancellationToken cancellationToken);
}

/// <summary>What a quarantine handler needs to know about one location.</summary>
/// <param name="Kind">The location kind, used to validate ledger states.</param>
/// <param name="TimeZoneId">The IANA timezone, for the business date.</param>
public sealed record QuarantineLocationInfo(
    LocationKind Kind,
    string TimeZoneId);