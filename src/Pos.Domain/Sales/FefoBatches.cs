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
    /// <param name="allowShortfall">
    /// Whether a request the shelf cannot cover is allocated anyway, with the
    /// uncovered remainder drawn from a batch that does not hold it. Off by
    /// default: the caller turns it on only where somebody has decided the draw
    /// may run the bucket negative, and the ledger still applies the location's
    /// negative-stock policy to what comes back.
    /// </param>
    /// <returns>
    /// The slices, one per batch consumed, in expiry order. Fails with
    /// <c>sale.item.quantity_invalid</c> when the requested quantity is not
    /// positive and with <c>inventory.insufficient_stock</c> when the available
    /// stock cannot cover the request and a shortfall is not allowed.
    /// </returns>
    public static Result<IReadOnlyList<AllocatedSlice>> Allocate(
        ProductId productId,
        LocationId locationId,
        decimal requested,
        IReadOnlyList<AllocatableBatch> batches,
        bool allowShortfall = false)
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
            if (!allowShortfall)
            {
                decimal availableQuantity = available.Sum(b => b.Quantity);
                return Result<IReadOnlyList<AllocatedSlice>>.Failure(
                    SaleErrors.InsufficientStock(productId, locationId, availableQuantity, requested));
            }

            decimal shortfall = decimal.Round(remaining, Quantity.Scale, MidpointRounding.AwayFromZero);

            if (slices.Count > 0)
            {
                // Added to the last slice rather than appended as a second one
                // against the same batch: the line draws more than that batch
                // holds, and saying so once is what the ledger has to see.
                slices[^1] = slices[^1] with { Quantity = slices[^1].Quantity + shortfall };
            }
            else
            {
                slices.Add(Shortfall(batches, shortfall));
            }
        }

        return Result<IReadOnlyList<AllocatedSlice>>.Success(slices);
    }

    /// <summary>
    /// Decides which batch carries what the shelf could not cover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing was drawn at all, so the batch FEFO would have finished on never
    /// made it into a slice. The same order still decides, over every batch
    /// offered including the emptied ones: the latest-expiring is the one most
    /// likely to be the stock actually standing there, because a shelf holding
    /// more than the system says is usually a receipt nobody recorded, and a
    /// receipt nobody recorded is recent.
    /// </para>
    /// <para>
    /// When there is no batch at all, the shortfall names none. That is a real
    /// answer rather than an evasion: the units left the shelf and the sale is a
    /// fact, so it is recorded against the product with the batch left open for
    /// whoever reconciles it — inventing one would put a number against a lot
    /// that never held it.
    /// </para>
    /// </remarks>
    private static AllocatedSlice Shortfall(IReadOnlyList<AllocatableBatch> batches, decimal quantity)
    {
        AllocatableBatch? carrier = batches
            .OrderBy(b => b.ExpiresOn ?? DateOnly.MaxValue)
            .ThenBy(b => b.BatchId?.Value ?? Guid.Empty)
            .LastOrDefault();

        return carrier is null
            ? new AllocatedSlice(null, null, quantity, 0m, null)
            : new AllocatedSlice(carrier.BatchId, carrier.BatchCode, quantity, carrier.UnitCost, carrier.ExpiresOn);
    }
}