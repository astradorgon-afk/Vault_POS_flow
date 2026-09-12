using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Inventory;

/// <summary>
/// Supplies the negative-stock policy from the location's own settings.
/// </summary>
/// <remarks>
/// <para>
/// A location whose settings have never been loaded, is missing, or whose stored
/// JSON is unreadable resolves to the strictest policy. Failing towards
/// <see cref="NegativeStockPolicy.Prohibit"/> means an unreadable configuration
/// can never silently widen who may drive stock below zero.
/// </para>
/// <para>
/// On a device this reads the synchronised local copy of the location row, so the
/// policy enforced offline is the one the server last published.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class LocationSettingsLedgerPolicyProvider(PosDbContext context) : ILedgerPolicyProvider
{
    /// <inheritdoc />
    public async Task<NegativeStockPolicy> GetNegativeStockPolicyAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        // The settings value converter already coalesces null and corrupt JSON to
        // LocationSettings.Default, so a missing row is the only remaining case.
        LocationSettings? settings = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => l.Settings)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return settings?.NegativeStockPolicy ?? NegativeStockPolicy.Prohibit;
    }
}