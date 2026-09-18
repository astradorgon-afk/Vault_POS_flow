using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Reports;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="IExceptionReportRepository" />
/// <remarks>
/// <para>
/// Which movement types count as a loss is declared once, here, rather than
/// inferred from the sign of a quantity. A transfer dispatch and a count
/// correction both reduce a bucket; only one of them is stock the business no
/// longer has.
/// </para>
/// <para>
/// Values come off the ledger leg, which recorded what the stock was carried at
/// when it left. Re-valuing a loss at today's cost would move a closed month's
/// shrinkage figure every time a supplier changed a price, and it would stop
/// reconciling to the accounts it exists to explain.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class ExceptionReportRepository(PosDbContext context) : IExceptionReportRepository
{
    /// <summary>
    /// The movement types that correct or write stock off.
    /// </summary>
    /// <remarks>
    /// Transfers, sales and receipts are excluded deliberately: stock moving to
    /// another location or out through a till is not an adjustment, however much
    /// it looks like one from the sign of the leg alone.
    /// </remarks>
    private static readonly InventoryMovementType[] AdjustmentTypes =
    [
        InventoryMovementType.Damage,
        InventoryMovementType.Spoilage,
        InventoryMovementType.Loss,
        InventoryMovementType.Theft,
        InventoryMovementType.ExpiryWriteOff,
        InventoryMovementType.CountAdjustmentIncrease,
        InventoryMovementType.CountAdjustmentDecrease,
        InventoryMovementType.ApprovedStockAdjustment,
        InventoryMovementType.TransitVarianceWriteOff,
    ];

    /// <summary>
    /// The subset of those that are a loss rather than a correction.
    /// </summary>
    /// <remarks>
    /// A count adjustment is excluded from shrinkage even when it reduces stock.
    /// It says the books were wrong, not that goods left the building, and folding
    /// it in would let a business shrink its shrinkage by counting more often.
    /// </remarks>
    private static readonly InventoryMovementType[] LossTypes =
    [
        InventoryMovementType.Damage,
        InventoryMovementType.Spoilage,
        InventoryMovementType.Loss,
        InventoryMovementType.Theft,
        InventoryMovementType.ExpiryWriteOff,
        InventoryMovementType.TransitVarianceWriteOff,
    ];

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdjustmentSummaryRow>> GetAdjustmentsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken)
    {
        List<Leg> legs = await LegsAsync(fromUtc, toUtc, locations, AdjustmentTypes, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, string> codes = await CodesAsync(legs, cancellationToken).ConfigureAwait(false);

        return
        [
            .. legs
                .GroupBy(l => (l.MovementType, l.ReasonCode, l.LocationId))
                .Select(g => new AdjustmentSummaryRow(
                    g.Key.MovementType,
                    g.Key.ReasonCode,
                    g.Key.LocationId,
                    Code(codes, g.Key.LocationId),
                    g.Count(),
                    g.Where(l => l.QuantityDelta < 0m).Sum(l => -l.QuantityDelta),
                    g.Where(l => l.QuantityDelta > 0m).Sum(l => l.QuantityDelta)))
                .OrderByDescending(r => r.QuantityOut)
                .ThenBy(r => r.MovementType)
                .ThenBy(r => r.LocationCode, StringComparer.Ordinal),
        ];
    }

    /// <inheritdoc />
    public async Task<ShrinkageReport> GetShrinkageAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken)
    {
        // Only the legs that took stock away. A write-off partly reversed leaves
        // both legs on the ledger, and counting the reversal as a loss would
        // report the same goods missing twice.
        List<Leg> legs =
        [
            .. (await LegsAsync(fromUtc, toUtc, locations, LossTypes, cancellationToken).ConfigureAwait(false))
                .Where(l => l.QuantityDelta < 0m),
        ];

        Dictionary<Guid, string> codes = await CodesAsync(legs, cancellationToken).ConfigureAwait(false);

        List<ShrinkageRow> rows =
        [
            .. legs
                .GroupBy(l => (l.MovementType, l.ReasonCode, l.LocationId))
                .Select(g => new ShrinkageRow(
                    g.Key.MovementType,
                    g.Key.ReasonCode,
                    g.Key.LocationId,
                    Code(codes, g.Key.LocationId),
                    g.Count(),
                    g.Sum(l => -l.QuantityDelta),
                    g.Sum(l => -l.TotalValueDelta)))
                .OrderByDescending(r => r.Value)
                .ThenBy(r => r.MovementType)
                .ThenBy(r => r.LocationCode, StringComparer.Ordinal),
        ];

        return new ShrinkageReport(
            fromUtc,
            toUtc,
            rows.Sum(r => r.Quantity),
            rows.Sum(r => r.Value),
            rows);
    }

    /// <inheritdoc />
    public async Task<CountVarianceReport> GetCountVariancesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        bool includeUncounted,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryCount> counts = context.InventoryCounts
            .AsNoTracking()
            .Include(c => c.Lines)
            .Where(c => c.CreatedAtUtc >= fromUtc && c.CreatedAtUtc <= toUtc);

        if (locations.Count > 0)
        {
            counts = counts.Where(c => locations.Contains(c.LocationId));
        }

        List<InventoryCount> taken = await counts.ToListAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<Guid, string> codes = await LocationCodesAsync(
            [.. taken.Select(c => c.LocationId).Distinct()], cancellationToken).ConfigureAwait(false);

        List<ProductId> productIds = [.. taken.SelectMany(c => c.Lines).Select(l => l.ProductId).Distinct()];

        var products = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { Id = p.Id.Value, p.Sku, p.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, (string Sku, string Name)> named =
            products.ToDictionary(p => p.Id, p => (p.Sku.Value, p.Name));

        List<CountVarianceRow> all =
        [
            .. taken
                .SelectMany(count => count.Lines.Select(line => Row(count, line, codes, named)))

                // A line nobody counted is not a variance of zero, and a line
                // counted and found right is not a variance at all.
                .Where(r => includeUncounted
                    ? r.Variance is null or not 0m
                    : r.Variance is not null && r.Variance != 0m)
                .OrderByDescending(r => Math.Abs(r.Variance ?? 0m))
                .ThenByDescending(r => r.IsRepeatVariance)
                .ThenBy(r => r.Sku, StringComparer.Ordinal),
        ];

        List<CountVarianceRow> rows = [.. all.Take(limit)];

        return new CountVarianceReport(fromUtc, toUtc, rows, Truncated: all.Count > rows.Count);
    }

    private static CountVarianceRow Row(
        InventoryCount count,
        InventoryCountLine line,
        Dictionary<Guid, string> codes,
        Dictionary<Guid, (string Sku, string Name)> products)
    {
        decimal? variance = line.PhysicalQuantity is { } physical ? physical - line.SystemQuantity : null;

        (string sku, string name) = products.TryGetValue(line.ProductId.Value, out (string Sku, string Name) found)
            ? found
            : (string.Empty, string.Empty);

        return new CountVarianceRow(
            count.Id.Value,
            count.Number,
            count.LocationId.Value,
            Code(codes, count.LocationId.Value),
            count.Status,
            line.CountedAtUtc,
            line.ProductId.Value,
            sku,
            name,
            line.SystemQuantity,
            line.PhysicalQuantity,
            variance,
            variance is { } gap ? decimal.Round(gap * line.UnitCost, Money.StorageScale, MidpointRounding.AwayFromZero) : null,
            line.IsRepeatVariance);
    }

    private async Task<List<Leg>> LegsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<LocationId> locations,
        InventoryMovementType[] types,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryMovement> movements = context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.RecordedAtUtc >= fromUtc
                        && m.RecordedAtUtc <= toUtc
                        && types.Contains(m.MovementType)
                        && m.State != InventoryState.External);

        if (locations.Count > 0)
        {
            movements = movements.Where(m => locations.Contains(m.LocationId));
        }

        var rows = await movements
            .Select(m => new
            {
                m.MovementType,
                m.ReasonCode,
                m.LocationId,
                m.QuantityDelta,
                m.TotalValueDelta,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new Leg(
                r.MovementType, r.ReasonCode, r.LocationId.Value, r.QuantityDelta, r.TotalValueDelta)),
        ];
    }

    private Task<Dictionary<Guid, string>> CodesAsync(List<Leg> legs, CancellationToken cancellationToken)
        => LocationCodesAsync([.. legs.Select(l => new LocationId(l.LocationId)).Distinct()], cancellationToken);

    private async Task<Dictionary<Guid, string>> LocationCodesAsync(
        List<LocationId> ids,
        CancellationToken cancellationToken)
    {
        var rows = await context.Locations
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new { Id = l.Id.Value, l.Code })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => r.Code);
    }

    /// <summary>A location's code, or empty where it has gone.</summary>
    /// <remarks>
    /// A store closed since still lost what it lost. Dropping the row would make a
    /// shrinkage total disagree with the ledger it was read from.
    /// </remarks>
    private static string Code(Dictionary<Guid, string> codes, Guid id)
        => codes.TryGetValue(id, out string? code) ? code : string.Empty;

    private sealed record Leg(
        InventoryMovementType MovementType,
        AdjustmentReasonCode? ReasonCode,
        Guid LocationId,
        decimal QuantityDelta,
        decimal TotalValueDelta);
}
