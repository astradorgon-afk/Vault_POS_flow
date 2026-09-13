using Microsoft.EntityFrameworkCore;
using Pos.Application.Quarantine;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Organizations;
using Pos.Domain.Quarantine;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Persists quarantine incidents. Mutations opt into tracking explicitly because
/// the context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class QuarantineRepository(PosDbContext context) : IQuarantineRepository
{
    /// <inheritdoc />
    public async Task<Result<QuarantineIncidentId>> AddAsync(
        QuarantineIncident incident,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);

        context.QuarantineIncidents.Add(incident);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<QuarantineIncidentId>.Success(incident.Id);
    }

    /// <inheritdoc />
    public Task<QuarantineIncident?> GetByIdAsync(
        QuarantineIncidentId incidentId,
        CancellationToken cancellationToken)
        => context.QuarantineIncidents
            .AsTracking()
            .Include(i => i.Lines)
            .Include(i => i.Photos)
            .Include(i => i.Timeline)
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

    /// <inheritdoc />
    public async Task<QuarantineLocationInfo?> GetLocationInfoAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        return location is null ? null : new QuarantineLocationInfo(location.Kind, location.TimeZoneId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, Product>> GetProductsByBarcodesAsync(
        IReadOnlyCollection<string> barcodes,
        CancellationToken cancellationToken)
    {
        if (barcodes.Count == 0)
        {
            return new Dictionary<string, Product>();
        }

        List<ProductBarcode> matches = await context.ProductBarcodes
            .AsNoTracking()
            .Where(b => barcodes.Contains(b.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return new Dictionary<string, Product>();
        }

        List<ProductId> productIds = [.. matches.Select(b => b.ProductId).Distinct()];

        List<Product> products = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ProductId, Product> productById = products.ToDictionary(p => p.Id);

        return matches
            .Where(b => productById.ContainsKey(b.ProductId))
            .ToDictionary(b => b.Value, b => productById[b.ProductId]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        // Barcodes are loaded so the link command can verify that the line's
        // scanned code belongs to the product being linked.
        return await context.Products
            .AsNoTracking()
            .Include(p => p.Barcodes)
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LocationId?> GetExternalLocationIdAsync(
        string systemCode,
        CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Code == systemCode, cancellationToken)
            .ConfigureAwait(false);

        return location?.Id;
    }
}