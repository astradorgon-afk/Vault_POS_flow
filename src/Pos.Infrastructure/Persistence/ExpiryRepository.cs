using Microsoft.EntityFrameworkCore;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Reads expiry facts from the database.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class ExpiryRepository(PosDbContext context) : IExpiryRepository
{
    /// <inheritdoc />
    public Task<LocationId?> GetExternalWriteOffLocationIdAsync(CancellationToken cancellationToken)
        => context.Locations
            .AsNoTracking()
            .Where(l => l.Code == SystemLocationCodes.ExternalWriteOff)
            .Select(l => (LocationId?)l.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ExpiryLocationInfo?> GetLocationAsync(LocationId locationId, CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        return location is null
            ? null
            : ToExpiryLocationInfo(location);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiryLocationInfo>> GetAllStockingLocationsAsync(CancellationToken cancellationToken)
    {
        List<Location> locations = await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind != LocationKind.External)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return locations.Select(ToExpiryLocationInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<int> GetExpiryWarningDaysAsync(LocationId locationId, CancellationToken cancellationToken)
    {
        LocationSettings? settings = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => l.Settings)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return settings?.ExpiryWarningDays ?? LocationSettings.Default.ExpiryWarningDays;
    }

    private static ExpiryLocationInfo ToExpiryLocationInfo(Location location)
        => new(
            location.Id,
            location.Kind,
            string.IsNullOrWhiteSpace(location.TimeZoneId) ? null : location.TimeZoneId,
            location.Settings?.ExpiryWarningDays ?? LocationSettings.Default.ExpiryWarningDays);
}