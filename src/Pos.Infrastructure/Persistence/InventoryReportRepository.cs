using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Reports;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="IInventoryReportRepository" />
/// <remarks>
/// <para>
/// Pure reads over the balances the ledger maintains and the legs it appended.
/// Nothing is re-derived: a report that recomputes a balance from the movements
/// would sooner or later disagree with the number the till refuses a sale on, and
/// then there would be two answers to one question.
/// </para>
/// <para>
/// The state buckets are rolled up here rather than in SQL. A pivot to a column
/// per state would have to be rewritten every time a state is added, and the
/// domain already says which states are on hand and which are in flight
/// (<see cref="InventoryStates" />) — reproducing that in a query is how the two
/// definitions drift apart.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class InventoryReportRepository(PosDbContext context) : IInventoryReportRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryOnHandRow>> GetOnHandAsync(
        InventoryReportQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<Bucket> buckets = await BucketsAsync(query, cancellationToken).ConfigureAwait(false);

        List<InventoryOnHandRow> rows =
        [
            .. buckets
                .GroupBy(b => (b.ProductId, b.LocationId))
                .Select(Row)
                .Where(r => query.IncludeEmpty || r.OnHand != 0m || r.InFlight != 0m)
                .OrderByDescending(r => r.OnHand)
                .ThenBy(r => r.Sku, StringComparer.Ordinal)
                .ThenBy(r => r.LocationCode, StringComparer.Ordinal),
        ];

        return [.. rows.Take(query.Limit)];
    }

    /// <inheritdoc />
    public async Task<InventoryValuationReport> GetValuationAsync(
        InventoryReportQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<Bucket> buckets = await BucketsAsync(query, cancellationToken).ConfigureAwait(false);

        List<InventoryValuationRow> all =
        [
            .. buckets
                .GroupBy(b => (b.ProductId, b.LocationId))
                .Select(ValuationRow)
                .Where(r => query.IncludeEmpty || r.Quantity != 0m || r.TotalValue != 0m)
                .OrderByDescending(r => r.TotalValue)
                .ThenBy(r => r.Sku, StringComparer.Ordinal)
                .ThenBy(r => r.LocationCode, StringComparer.Ordinal),
        ];

        List<InventoryValuationRow> rows = [.. all.Take(query.Limit)];

        // Totalled over everything that matched, not over the page. A valuation
        // whose total only covers the rows that fitted is one somebody will put in
        // a set of accounts.
        return new InventoryValuationReport(
            DateTimeOffset.UtcNow,
            all.Sum(r => r.Quantity),
            all.Sum(r => r.TotalValue),
            rows,
            Truncated: all.Count > rows.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryAgeingRow>> GetAgeingAsync(
        DateOnly asOf,
        InventoryReportQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<Bucket> buckets = await BucketsAsync(query, cancellationToken).ConfigureAwait(false);

        // Only stock physically standing somewhere has an age worth reporting.
        // Stock in transit is somebody's problem today, not stock that has been
        // sitting since spring.
        List<Bucket> onHand = [.. buckets.Where(b => InventoryStates.OnHand.Contains(b.State) && b.Quantity != 0m)];

        List<BatchId> batchIds =
        [
            .. onHand.Select(b => b.BatchKey).Where(id => id != Guid.Empty).Distinct().Select(id => new BatchId(id)),
        ];

        Dictionary<Guid, DateOnly> received = await context.Batches
            .AsNoTracking()
            .Where(b => batchIds.Contains(b.Id))
            .Select(b => new { Id = b.Id.Value, b.ReceivedOn })
            .ToDictionaryAsync(b => b.Id, b => b.ReceivedOn, cancellationToken)
            .ConfigureAwait(false);

        List<InventoryAgeingRow> rows =
        [
            .. onHand
                .GroupBy(b => (b.ProductId, b.LocationId))
                .Select(g => AgeingRow(g, asOf, received))
                .OrderBy(r => r.OldestReceivedOn ?? DateOnly.MaxValue)
                .ThenByDescending(r => r.OnHand)
                .ThenBy(r => r.Sku, StringComparer.Ordinal),
        ];

        return [.. rows.Take(query.Limit)];
    }

    /// <inheritdoc />
    public async Task<InventoryDeadStockReport> GetDeadStockAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        InventoryReportQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<Bucket> buckets = await BucketsAsync(query, cancellationToken).ConfigureAwait(false);

        var held = buckets
            .Where(b => InventoryStates.OnHand.Contains(b.State))
            .GroupBy(b => (b.ProductId, b.LocationId))
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.LocationId,
                First = g.First(),
                OnHand = g.Sum(b => b.Quantity),
            })

            // Nothing standing there is not dead stock, it is absent. The question
            // this answers is what is sitting on a shelf not selling.
            .Where(r => r.OnHand > 0m)
            .ToList();

        Dictionary<(Guid, Guid), Sold> sales = await SalesAsync(fromUtc, toUtc, query, cancellationToken)
            .ConfigureAwait(false);

        decimal windowDays = Math.Max(1m, (decimal)(toUtc - fromUtc).TotalDays);

        List<InventoryDeadStockRow> all =
        [
            .. held
                .Select(r =>
                {
                    sales.TryGetValue((r.ProductId, r.LocationId), out Sold? sold);
                    return DeadStockRow(r.First, r.OnHand, sold, toUtc, windowDays);
                })

                // Never sold sorts first, then longest unsold. A filter written as
                // "last sold before X" would drop exactly what this exists to find.
                .OrderByDescending(r => r.DaysSinceLastSale ?? int.MaxValue)
                .ThenByDescending(r => r.OnHand)
                .ThenBy(r => r.Sku, StringComparer.Ordinal),
        ];

        List<InventoryDeadStockRow> rows = [.. all.Take(query.Limit)];

        return new InventoryDeadStockReport(fromUtc, toUtc, rows, Truncated: all.Count > rows.Count);
    }

    /// <summary>
    /// What sold, and when it last did, per product and location.
    /// </summary>
    /// <remarks>
    /// Read from the ledger rather than from sale lines: a sale is not the only
    /// way stock leaves a shelf, but it is the only one that counts as moving —
    /// a write-off clearing a dead line would otherwise make it look alive.
    /// </remarks>
    private async Task<Dictionary<(Guid, Guid), Sold>> SalesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        InventoryReportQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryMovement> sales = context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.MovementType == InventoryMovementType.PosSale
                        && m.QuantityDelta < 0m
                        && m.State == InventoryState.Available
                        && m.RecordedAtUtc >= fromUtc
                        && m.RecordedAtUtc <= toUtc);

        if (query.Locations.Count > 0)
        {
            sales = sales.Where(m => query.Locations.Contains(m.LocationId));
        }

        if (query.ProductId is { } productId)
        {
            sales = sales.Where(m => m.ProductId == productId);
        }

        var rows = await sales
            .GroupBy(m => new { m.ProductId, m.LocationId })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.LocationId,
                Units = g.Sum(m => -m.QuantityDelta),
                LastAt = g.Max(m => m.RecordedAtUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(
            r => (r.ProductId.Value, r.LocationId.Value),
            r => new Sold(r.Units, r.LastAt));
    }

    private static InventoryAgeingRow AgeingRow(
        IGrouping<(Guid ProductId, Guid LocationId), Bucket> g,
        DateOnly asOf,
        Dictionary<Guid, DateOnly> received)
    {
        Bucket first = g.First();

        List<(InventoryAgeBucket Bucket, decimal Quantity, DateOnly? ReceivedOn)> aged =
        [
            .. g.Select(b =>
            {
                DateOnly? on = b.BatchKey != Guid.Empty && received.TryGetValue(b.BatchKey, out DateOnly found)
                    ? found
                    : null;

                return (Age(on, asOf), b.Quantity, on);
            }),
        ];

        return new InventoryAgeingRow(
            first.ProductId,
            first.Sku,
            first.ProductName,
            first.LocationId,
            first.LocationCode,
            aged.Sum(a => a.Quantity),
            aged.Where(a => a.ReceivedOn is not null).Min(a => a.ReceivedOn),
            [
                .. aged
                    .GroupBy(a => a.Bucket)
                    .Select(b => new InventoryAgeQuantity(b.Key, b.Sum(a => a.Quantity)))
                    .Where(b => b.Quantity != 0m)
                    .OrderByDescending(b => b.Bucket),
            ]);
    }

    /// <summary>Places one holding in its age bucket.</summary>
    private static InventoryAgeBucket Age(DateOnly? receivedOn, DateOnly asOf)
    {
        if (receivedOn is not { } on)
        {
            // Genuinely unknown, not zero. A product that tracks no batches has no
            // received date anywhere in the system, and calling it new would make
            // the report say the opposite of the truth about the oldest stock.
            return InventoryAgeBucket.Unknown;
        }

        int days = asOf.DayNumber - on.DayNumber;

        return days switch
        {
            <= 30 => InventoryAgeBucket.UpTo30Days,
            <= 60 => InventoryAgeBucket.Days31To60,
            <= 90 => InventoryAgeBucket.Days61To90,
            <= 180 => InventoryAgeBucket.Days91To180,
            _ => InventoryAgeBucket.Over180Days,
        };
    }

    private static InventoryDeadStockRow DeadStockRow(
        Bucket first,
        decimal onHand,
        Sold? sold,
        DateTimeOffset toUtc,
        decimal windowDays)
    {
        decimal units = sold?.Units ?? 0m;

        // No rate to divide by. Reporting infinity as a large number is how a line
        // nobody can shift ends up looking merely slow.
        decimal? cover = units <= 0m
            ? null
            : decimal.Round(onHand / (units / windowDays), 1, MidpointRounding.AwayFromZero);

        return new InventoryDeadStockRow(
            first.ProductId,
            first.Sku,
            first.ProductName,
            first.LocationId,
            first.LocationCode,
            onHand,
            units,
            sold?.LastAtUtc,
            sold is null ? null : (int)Math.Floor((toUtc - sold.LastAtUtc).TotalDays),
            cover);
    }

    private sealed record Sold(decimal Units, DateTimeOffset LastAtUtc);

    /// <inheritdoc />
    public async Task<InventoryMovementReport> GetMovementsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        InventoryReportQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<InventoryMovement> movements = context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.RecordedAtUtc >= fromUtc && m.RecordedAtUtc <= toUtc);

        if (query.Locations.Count > 0)
        {
            movements = movements.Where(m => query.Locations.Contains(m.LocationId));
        }

        if (query.ProductId is { } productId)
        {
            movements = movements.Where(m => m.ProductId == productId);
        }

        // One more than asked for, so "there is more behind this" is something the
        // query answered rather than something the caller has to guess from a
        // suspiciously round count.
        var page = await (
            from movement in movements
            join product in context.Products.AsNoTracking() on movement.ProductId equals product.Id
            join location in context.Locations.AsNoTracking() on movement.LocationId equals location.Id

            // The counterparty leg is excluded, as it is from the shelf counts.
            // Every sale and every delivery has one, and it is bookkeeping rather
            // than stock moving at a place somebody works: listing it would double
            // the length of every history and answer no question anybody asked.
            where location.Kind != LocationKind.External
            orderby movement.RecordedAtUtc, movement.LegNumber
            select new
            {
                movement.OccurredAtUtc,
                movement.RecordedAtUtc,
                movement.ProductId,
                product.Sku,
                ProductName = product.Name,
                movement.LocationId,
                location.Code,
                movement.State,
                movement.QuantityDelta,
                movement.MovementType,
                movement.ReferenceDocumentType,
                movement.ReferenceNumber,
                movement.BatchId,
                movement.CreatedByUserId,
            })
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool truncated = page.Count > query.Limit;

        return new InventoryMovementReport(
            fromUtc,
            toUtc,
            [
                .. page.Take(query.Limit).Select(m => new InventoryMovementRow(
                    m.OccurredAtUtc,
                    m.RecordedAtUtc,
                    m.ProductId.Value,
                    m.Sku.Value,
                    m.ProductName,
                    m.LocationId.Value,
                    m.Code,
                    m.State,
                    m.QuantityDelta,
                    m.MovementType,
                    m.ReferenceDocumentType,
                    m.ReferenceNumber,
                    m.BatchId?.Value,
                    m.CreatedByUserId.Value)),
            ],
            truncated);
    }

    /// <summary>
    /// Every balance bucket in scope, with the names a report needs.
    /// </summary>
    /// <remarks>
    /// External locations are excluded. Their buckets are the other leg of every
    /// sale and delivery — the counterparty the business trades with — and
    /// counting them as stock would report the whole history of everything ever
    /// sold as sitting on a shelf.
    /// </remarks>
    private async Task<List<Bucket>> BucketsAsync(
        InventoryReportQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryBalance> balances = context.InventoryBalances.AsNoTracking();

        if (query.Locations.Count > 0)
        {
            balances = balances.Where(b => query.Locations.Contains(b.LocationId));
        }

        if (query.ProductId is { } productId)
        {
            balances = balances.Where(b => b.ProductId == productId);
        }

        var rows = await (
            from balance in balances
            join product in context.Products.AsNoTracking() on balance.ProductId equals product.Id
            join location in context.Locations.AsNoTracking() on balance.LocationId equals location.Id
            where location.Kind != LocationKind.External
            select new
            {
                balance.ProductId,
                product.Sku,
                ProductName = product.Name,
                balance.LocationId,
                location.Code,
                LocationName = location.Name,
                balance.BatchKey,
                balance.State,
                balance.Quantity,
                balance.TotalValue,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new Bucket(
                r.ProductId.Value,
                r.Sku.Value,
                r.ProductName,
                r.LocationId.Value,
                r.Code,
                r.LocationName,
                r.BatchKey.Value,
                r.State,
                r.Quantity,
                r.TotalValue)),
        ];
    }

    private static InventoryOnHandRow Row(IGrouping<(Guid ProductId, Guid LocationId), Bucket> g)
    {
        Bucket first = g.First();

        return new InventoryOnHandRow(
            first.ProductId,
            first.Sku,
            first.ProductName,
            first.LocationId,
            first.LocationCode,
            first.LocationName,
            g.Where(b => b.State == InventoryState.Available).Sum(b => b.Quantity),
            g.Where(b => InventoryStates.OnHand.Contains(b.State)).Sum(b => b.Quantity),
            g.Where(b => InventoryStates.InFlight.Contains(b.State)).Sum(b => b.Quantity),
            [
                .. g.Where(b => b.Quantity != 0m)
                    .OrderBy(b => b.State)
                    .Select(b => new InventoryStateQuantity(b.State, b.Quantity)),
            ]);
    }

    private static InventoryValuationRow ValuationRow(IGrouping<(Guid ProductId, Guid LocationId), Bucket> g)
    {
        Bucket first = g.First();
        decimal quantity = g.Sum(b => b.Quantity);
        decimal value = g.Sum(b => b.TotalValue);

        // Derived from the totals, never averaged from the buckets: an average of
        // averages weights a batch holding one unit the same as one holding a
        // thousand. With no units there is no cost per unit to state, and the
        // residual value is a rounding artefact to investigate rather than a
        // division to attempt.
        decimal unitCost = quantity == 0m
            ? 0m
            : decimal.Round(value / quantity, Money.StorageScale, MidpointRounding.AwayFromZero);

        return new InventoryValuationRow(
            first.ProductId,
            first.Sku,
            first.ProductName,
            first.LocationId,
            first.LocationCode,
            first.LocationName,
            quantity,
            unitCost,
            value);
    }

    private sealed record Bucket(
        Guid ProductId,
        string Sku,
        string ProductName,
        Guid LocationId,
        string LocationCode,
        string LocationName,
        Guid BatchKey,
        InventoryState State,
        decimal Quantity,
        decimal TotalValue);
}
