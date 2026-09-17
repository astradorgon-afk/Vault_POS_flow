using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Organizations;
using Pos.Infrastructure.Inventory;

namespace Pos.Infrastructure.Offline;

/// <summary>
/// Supplies the negative-stock policy from the location settings the device
/// cached, so an offline post honours the owner's configuration rather than a
/// guess.
/// </summary>
/// <remarks>
/// A location the device has never heard of, or one whose settings the feed has
/// not carried, reads as <see cref="NegativeStockPolicy.Prohibit"/> — the
/// strictest option, and the same answer <see cref="StrictLedgerPolicyProvider"/>
/// gives. Losing a connection must not let a register sell stock it does not
/// have.
/// </remarks>
/// <param name="context">The scoped device context.</param>
public sealed class DeviceLedgerPolicyProvider(PosDeviceDbContext context) : ILedgerPolicyProvider
{
    /// <inheritdoc />
    public async Task<NegativeStockPolicy> GetNegativeStockPolicyAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        string? settingsJson = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => l.SettingsJson)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return LocationSettings.FromJson(settingsJson).NegativeStockPolicy;
    }
}
