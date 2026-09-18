using Microsoft.EntityFrameworkCore;
using Pos.Application.Common.Abstractions;
using Pos.Application.Reports;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Reports;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="IDashboardRepository" />
/// <remarks>
/// <para>
/// The sales half is composed from <see cref="ISalesAnalysisRepository" /> rather
/// than queried again. A dashboard that ran its own version of the sales
/// arithmetic would eventually disagree with the report a manager opens to check
/// it, and the person looking at two different numbers for one week has no way to
/// tell which is wrong. Being the same code is the only guarantee that holds.
/// </para>
/// <para>
/// The stock half is a snapshot of now, whatever period the sales figures cover.
/// Saying so in the payload matters: mixing the two silently is how somebody
/// concludes last month's sales emptied a shelf that was restocked on Tuesday.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
/// <param name="sales">The one sales analysis, shared with the reports.</param>
/// <param name="clock">The authoritative clock.</param>
public sealed class DashboardRepository(
    PosDbContext context,
    ISalesAnalysisRepository sales,
    ISystemClock clock) : IDashboardRepository
{
    /// <inheritdoc />
    public async Task<DashboardOverview> GetOverviewAsync(
        DashboardQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        SalesAnalysisReport byLocation = await sales
            .GetSalesAnalysisAsync(
                new SalesAnalysisQuery(
                    query.FromDate,
                    query.ToDate,
                    SalesAnalysisGrouping.Location,
                    query.Locations,

                    // Every store in scope, because a comparison missing its tail
                    // is a league table that lies about who is last.
                    int.MaxValue),
                cancellationToken)
            .ConfigureAwait(false);

        DashboardKpis whole = Kpis(byLocation.Totals, query.IncludeFinancial);

        Dictionary<Guid, string> names = await NamesAsync(
            [.. byLocation.Rows.Select(r => new LocationId(r.GroupId))], cancellationToken).ConfigureAwait(false);

        List<DashboardStoreRow> stores =
        [
            .. byLocation.Rows.Select(row => new DashboardStoreRow(
                row.GroupId,
                row.GroupCode,
                names.TryGetValue(row.GroupId, out string? name) ? name : string.Empty,
                Kpis(row, query.IncludeFinancial),

                // Null rather than zero when nothing sold anywhere: a share of
                // nothing is not nought per cent, it is not a share.
                whole.Revenue == 0m ? null : decimal.Round(row.Revenue / whole.Revenue, 4, MidpointRounding.AwayFromZero))),
        ];

        return new DashboardOverview(
            query.Range,
            query.FromDate,
            query.ToDate,
            query.TimeZoneId,
            whole,
            stores,
            await InventoryAsync(query, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Turns a sales analysis row into the dashboard's headline shape.
    /// </summary>
    /// <remarks>
    /// Cost, profit and margin come back null for a caller without the financial
    /// permission rather than zero. Zero reads as "we made nothing", which is a
    /// statement about the business rather than about the reader.
    /// </remarks>
    private static DashboardKpis Kpis(SalesAnalysisRow row, bool includeFinancial) => new(
        row.SaleCount,
        row.GrossRevenue,
        row.Discount,
        row.Revenue,
        includeFinancial ? row.Cost : null,
        includeFinancial ? row.GrossProfit : null,
        includeFinancial ? row.MarginPercent : null,
        row.QuantitySold,
        row.QuantityReturned,
        row.SaleCount == 0
            ? null
            : decimal.Round(row.Revenue / row.SaleCount, Money.StorageScale, MidpointRounding.AwayFromZero));

    private async Task<DashboardInventoryPanel> InventoryAsync(
        DashboardQuery query,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryBalance> balances = context.InventoryBalances.AsNoTracking();

        if (query.Locations.Count > 0)
        {
            balances = balances.Where(b => query.Locations.Contains(b.LocationId));
        }

        var rows = await (
            from balance in balances
            join location in context.Locations.AsNoTracking() on balance.LocationId equals location.Id

            // Counterparty buckets are the other leg of every sale ever rung up;
            // counting them as stock would report the shop's whole history as
            // sitting on a shelf.
            where location.Kind != LocationKind.External
            select new { balance.ProductId, balance.LocationId, balance.State, balance.Quantity, balance.TotalValue })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<Bucket> buckets =
        [
            .. rows.Select(r => new Bucket(r.ProductId, r.LocationId, r.State, r.Quantity, r.TotalValue)),
        ];

        Dictionary<(ProductId, LocationId), decimal> available = buckets
            .Where(b => b.State == InventoryState.Available)
            .GroupBy(b => (b.ProductId, b.LocationId))
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));

        IQueryable<ProductLocationSetting> settings = context.ProductLocationSettings
            .AsNoTracking()
            .Where(s => s.IsStocked);

        if (query.Locations.Count > 0)
        {
            settings = settings.Where(s => query.Locations.Contains(s.LocationId));
        }

        var stocked = await settings
            .Select(s => new { s.ProductId, s.LocationId, s.ReorderPoint, s.MaximumStock })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int outOfStock = 0;
        int low = 0;
        int over = 0;

        foreach (var line in stocked)
        {
            decimal onHand = available.TryGetValue((line.ProductId, line.LocationId), out decimal quantity)
                ? quantity
                : 0m;

            // Out of stock and low are told apart rather than rolled together: one
            // is a sale being refused right now and the other is a sale that will
            // be refused next week, and they go to different people.
            if (onHand <= 0m)
            {
                outOfStock++;
            }
            else if (onHand <= line.ReorderPoint)
            {
                low++;
            }

            if (line.MaximumStock > 0m && onHand > line.MaximumStock)
            {
                over++;
            }
        }

        return new DashboardInventoryPanel(
            clock.UtcNow,
            buckets.Where(b => b.State == InventoryState.Available).Sum(b => b.Quantity),
            buckets.Where(b => b.State == InventoryState.InTransit).Sum(b => b.Quantity),
            buckets.Where(b => b.State == InventoryState.Quarantine).Sum(b => b.Quantity),

            // Everything else physically standing there: damaged, expired,
            // awaiting inspection, a return not yet dispositioned. Named as one
            // number because the panel's question is how much is not sellable,
            // and the breakdown is what the on-hand report is for.
            buckets.Where(b =>
                b.State != InventoryState.Available
                && b.State != InventoryState.Quarantine
                && InventoryStates.OnHand.Contains(b.State)).Sum(b => b.Quantity),
            query.IncludeFinancial ? buckets.Sum(b => b.TotalValue) : null,
            outOfStock,
            low,
            over);
    }

    private sealed record Bucket(
        ProductId ProductId,
        LocationId LocationId,
        InventoryState State,
        decimal Quantity,
        decimal TotalValue);

    private async Task<Dictionary<Guid, string>> NamesAsync(
        List<LocationId> ids,
        CancellationToken cancellationToken)
    {
        var rows = await context.Locations
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new { Id = l.Id.Value, l.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => r.Name);
    }
}
