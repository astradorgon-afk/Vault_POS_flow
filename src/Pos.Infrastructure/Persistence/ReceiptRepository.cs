using Microsoft.EntityFrameworkCore;
using Pos.Application.Receipts;
using Pos.Domain.Common;
using Pos.Domain.Organizations;
using Pos.Domain.Receipts;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Persists payment receipts. Mutations opt into tracking explicitly because
/// the context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class ReceiptRepository(PosDbContext context) : IReceiptRepository
{
    /// <inheritdoc />
    public async Task<Result<ReceiptId>> AddAsync(
        Receipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        context.Receipts.Add(receipt);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<ReceiptId>.Success(receipt.Id);
    }

    /// <inheritdoc />
    public async Task<ReceiptLocationInfo?> GetLocationInfoAsync(
        LocationId locationId,
        CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        return location is null ? null : new ReceiptLocationInfo(location.Kind);
    }
}