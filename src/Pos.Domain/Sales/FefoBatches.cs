using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// A batch that a sale line may draw stock from.
/// </summary>
/// <param name="BatchId">The batch, or <see langword="null"/> for products that do not track batches.</param>
/// <param name="BatchCode">The human-readable batch or lot code, when tracked.</param>
/// <param name="Quantity">The quantity available to sell.</param>
/// <param name="UnitCost">The weighted average cost per unit at the location.</param>
/// <param name="ExpiresOn">The batch expiry date, when the product tracks expiry.</param>
public sealed record AllocatableBatch(
    BatchId? BatchId,
    string? BatchCode,
    decimal Quantity,
    decimal UnitCost,
    DateOnly? ExpiresOn);

/// <summary>One portion of a requested quantity drawn from a single batch.</summary>
/// <param name="BatchId">The batch, or <see langword="null"/> for products that do not track batches.</param>
/// <param name="BatchCode">The human-readable batch or lot code, when tracked.</param>
/// <param name="Quantity">The quantity allocated from this batch.</param>
/// <param name="UnitCost">The cost per unit of this batch.</param>
/// <param name="ExpiresOn">The batch expiry date, when the product tracks expiry.</param>
public sealed record AllocatedSlice(
    BatchId? BatchId,
    string? BatchCode,
    decimal Quantity,
    decimal UnitCost,
    DateOnly? ExpiresOn);

/// <summary>
/// First-expired-first-out batch allocation at a location (POS.md §2.3). The
/// caller provides the sellable batches — already filtered to exclude expired
/// stock — and this class consumes the requested quantity in expiry order so the
/// earliest-expiring stock leaves the shelf first.
/// </summary>
public static class FefoBatches
{
    /// <summary>
    /// Allocates a requested quantity across the available sellable batches.
    /// </summary>
    /// <param name="productId">The product being sold.</param>
    /// <param name="locationId">The location the sale happens at.</param>
    /// <param name="requested">The quantity requested, greater than zero.</param>
    /// <param name="batches">The sellable batches at the location, in any order.</param>
    /// <returns>
    /// The slices, one per batch consumed, in expiry order. Fails with
    /// <c>sale.item.quantity_invalid</c> when the requested quantity is not
    /// positive and with <c>inventory.insufficient_stock</c> when the available
    /// stock cannot cover the request.
    /// </returns>
    public static Result<IReadOnlyList<AllocatedSlice>> Allocate(
        ProductId productId,
        LocationId locationId,
        decimal requested,
        IReadOnlyList<AllocatableBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);

        if (requested <= 0m)
        {
            return Result<IReadOnlyList<AllocatedSlice>>.Failure(SaleErrors.ItemQuantityInvalid);
        }

        // FEFO: earliest expiry first. Batches without an expiry — a product that
        // does not track batches is modelled as one such row — come last, and the
        // batch identifier breaks ties deterministically.
        AllocatableBatch[] available = [.. batches
            .Where(b => b.Quantity > 0m)
            .OrderBy(b => b.ExpiresOn ?? DateOnly.MaxValue)
            .ThenBy(b => b.BatchId?.Value ?? Guid.Empty)];

        decimal remaining = requested;
        List<AllocatedSlice> slices = [];

        foreach (AllocatableBatch batch in available)
        {
            if (remaining <= 0m)
            {
                break;
            }

            decimal take = Math.Min(remaining, batch.Quantity);
            slices.Add(new AllocatedSlice(
                batch.BatchId,
                batch.BatchCode,
                decimal.Round(take, Quantity.Scale, MidpointRounding.AwayFromZero),
                batch.UnitCost,
                batch.ExpiresOn));
            remaining -= take;
        }

        if (remaining > 0m)
        {
            decimal availableQuantity = available.Sum(b => b.Quantity);
            return Result<IReadOnlyList<AllocatedSlice>>.Failure(
                SaleErrors.InsufficientStock(productId, locationId, availableQuantity, requested));
        }

        return Result<IReadOnlyList<AllocatedSlice>>.Success(slices);
    }
}