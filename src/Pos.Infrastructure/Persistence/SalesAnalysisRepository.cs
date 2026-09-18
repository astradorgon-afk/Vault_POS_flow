using Microsoft.EntityFrameworkCore;
using Pos.Application.Reports;
using Pos.Domain.Common;
using Pos.Domain.Reports;
using Pos.Domain.Sales;
using Pos.Infrastructure.Identity;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="ISalesAnalysisRepository" />
/// <remarks>
/// <para>
/// Every query is a pure read over completed sales. Each cut groups on its own
/// key and nothing else, and the names are resolved in a second pass: a single
/// query grouping by every key at once would return a row per product per cashier
/// per store, which is a table nobody asked for and the one that gets large.
/// </para>
/// <para>
/// The arithmetic that can divide by zero is done here rather than in SQL,
/// because a margin percentage on a product given away has to be a report that
/// says nothing was earned, not a report that throws.
/// </para>
/// <para>
/// Revenue is each line's net amount less the VAT collected on it. Deliberately
/// not the line's taxable base: that is zero on a VAT-exempt line by design, and
/// reporting everything a pharmacy sells to a senior citizen as having earned
/// nothing is a worse error than the tax-inclusive figure it was meant to fix.
/// </para>
/// </remarks>
/// <param name="context">The database context.</param>
public sealed class SalesAnalysisRepository(PosDbContext context) : ISalesAnalysisRepository
{
    /// <inheritdoc />
    public async Task<SalesAnalysisReport> GetSalesAnalysisAsync(
        SalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<Aggregate> aggregates = query.GroupBy switch
        {
            SalesAnalysisGrouping.Product => await ByProductAsync(query, cancellationToken).ConfigureAwait(false),
            SalesAnalysisGrouping.Category => await ByCategoryAsync(query, cancellationToken).ConfigureAwait(false),
            SalesAnalysisGrouping.Location => await ByLocationAsync(query, cancellationToken).ConfigureAwait(false),
            SalesAnalysisGrouping.Cashier => await ByCashierAsync(query, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };

        // Totalled from the same aggregates the rows come from, not from a second
        // query. A total that disagrees with the rows under it is the one defect a
        // reader cannot work around.
        SalesAnalysisRow totals = Row(new Aggregate(
            Guid.Empty,
            string.Empty,
            "All",
            aggregates.Sum(a => a.SaleCount),
            aggregates.Sum(a => a.QuantitySold),
            aggregates.Sum(a => a.QuantityReturned),
            aggregates.Sum(a => a.GrossRevenue),
            aggregates.Sum(a => a.Discount),
            aggregates.Sum(a => a.Revenue),
            aggregates.Sum(a => a.Cost)));

        List<SalesAnalysisRow> rows =
        [
            .. aggregates
                .OrderByDescending(a => a.Revenue)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .Take(query.Limit)
                .Select(Row),
        ];

        return new SalesAnalysisReport(
            query.FromDate,
            query.ToDate,
            query.GroupBy,
            totals,
            rows,
            Truncated: aggregates.Count > rows.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SalesPaymentMethodRow>> GetPaymentMethodBreakdownAsync(
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyCollection<LocationId> locations,
        CancellationToken cancellationToken)
    {
        List<SalesPaymentMethodRow> rows = await (
            from sale in Sales(fromDate, toDate, locations)
            join payment in context.Payments.AsNoTracking()
                on sale.Id equals EF.Property<SaleId>(payment, "SaleId")
            group payment by payment.Method into g
            select new SalesPaymentMethodRow(
                g.Key.ToString(),
                g.Count(),
                g.Sum(p => p.Amount),
                g.Sum(p => p.Change) ?? 0m))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.OrderByDescending(r => r.Amount).ThenBy(r => r.Method, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The completed sales of the period, within the caller's stores.
    /// </summary>
    /// <remarks>
    /// An empty <paramref name="locations"/> is business-wide authority asking for
    /// everything, so no location filter is applied at all. Written as a branch
    /// rather than an <c>Any() ||</c> inside the predicate, because a caller who
    /// meant "no stores" and got "every store" is the mistake this shape exists to
    /// make impossible to write by accident.
    /// </remarks>
    private IQueryable<Sale> Sales(
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyCollection<LocationId> locations)
    {
        IQueryable<Sale> sales = context.Sales
            .AsNoTracking()
            .Where(s => s.Status == SaleStatus.Completed
                        && s.BusinessDate >= fromDate
                        && s.BusinessDate <= toDate);

        return locations.Count == 0 ? sales : sales.Where(s => locations.Contains(s.LocationId));
    }

    /// <summary>
    /// One row per product, keyed on the name the receipt carries.
    /// </summary>
    /// <remarks>
    /// The name comes off the sale line, where it was frozen at the till, rather
    /// than off the product as it stands now. A product renamed since is reported
    /// under what the receipt says, because that is what the person holding the
    /// receipt will ask about — and a rename mid-period splits the row, which is
    /// the honest answer rather than a silent merge under whichever name won.
    /// </remarks>
    private async Task<List<Aggregate>> ByProductAsync(
        SalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        List<ProductRow> grouped = await (
            from sale in Sales(query.FromDate, query.ToDate, query.Locations)
            join item in context.SaleItems.AsNoTracking() on sale.Id equals item.SaleId
            group new { sale, item } by new { item.ProductId, item.ProductName } into g
            select new ProductRow(
                g.Key.ProductId,
                g.Key.ProductName,
                g.Select(x => x.sale.Id).Distinct().Count(),
                g.Sum(x => x.item.Quantity),
                g.Sum(x => x.item.ReturnedQuantity),
                g.Sum(x => x.item.GrossAmount),
                g.Sum(x => x.item.Discount),
                g.Sum(x => x.item.NetAmount - x.item.Vat),
                g.Sum(x => x.item.UnitCost * x.item.Quantity)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. grouped.Select(p => new Aggregate(
            p.ProductId.Value,
            string.Empty,
            p.ProductName,
            p.SaleCount,
            p.QuantitySold,
            p.QuantityReturned,
            p.GrossRevenue,
            p.Discount,
            p.Revenue,
            p.Cost))];
    }

    /// <summary>
    /// One row per category, folded from the per-product rows.
    /// </summary>
    /// <remarks>
    /// Folded in memory rather than joined in SQL because the product rows are
    /// already bounded by what actually sold in the period, and a three-way join
    /// through the catalogue to group on a column the sale line does not carry
    /// buys nothing but a query plan.
    /// </remarks>
    private async Task<List<Aggregate>> ByCategoryAsync(
        SalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        List<Aggregate> products = await ByProductAsync(query, cancellationToken).ConfigureAwait(false);
        List<ProductId> productIds = [.. products.Select(p => new ProductId(p.Id)).Distinct()];

        var categories = await (
            from product in context.Products.AsNoTracking()
            join category in context.Categories.AsNoTracking() on product.CategoryId equals category.Id
            where productIds.Contains(product.Id)
            select new { ProductId = product.Id.Value, CategoryId = category.Id.Value, category.Code, category.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byProduct = categories.ToDictionary(c => c.ProductId);

        return
        [
            .. products
                .Where(p => byProduct.ContainsKey(p.Id))
                .GroupBy(p => byProduct[p.Id].CategoryId)
                .Select(g => Fold(
                    g.Key,
                    byProduct[g.First().Id].Code,
                    byProduct[g.First().Id].Name,
                    g)),
        ];
    }

    private async Task<List<Aggregate>> ByLocationAsync(
        SalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        List<KeyRow> grouped = await (
            from sale in Sales(query.FromDate, query.ToDate, query.Locations)
            join item in context.SaleItems.AsNoTracking() on sale.Id equals item.SaleId
            group new { sale, item } by sale.LocationId into g
            select new KeyRow(
                g.Key.Value,
                g.Select(x => x.sale.Id).Distinct().Count(),
                g.Sum(x => x.item.Quantity),
                g.Sum(x => x.item.ReturnedQuantity),
                g.Sum(x => x.item.GrossAmount),
                g.Sum(x => x.item.Discount),
                g.Sum(x => x.item.NetAmount - x.item.Vat),
                g.Sum(x => x.item.UnitCost * x.item.Quantity)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<LocationId> ids = [.. grouped.Select(g => new LocationId(g.Id))];

        var names = await context.Locations
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new { Id = l.Id.Value, l.Code, l.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Named(grouped, names.ToDictionary(n => n.Id, n => (n.Code, n.Name)));
    }

    private async Task<List<Aggregate>> ByCashierAsync(
        SalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        List<KeyRow> grouped = await (
            from sale in Sales(query.FromDate, query.ToDate, query.Locations)
            join item in context.SaleItems.AsNoTracking() on sale.Id equals item.SaleId
            group new { sale, item } by sale.CompletedByUserId into g
            select new KeyRow(
                g.Key.Value,
                g.Select(x => x.sale.Id).Distinct().Count(),
                g.Sum(x => x.item.Quantity),
                g.Sum(x => x.item.ReturnedQuantity),
                g.Sum(x => x.item.GrossAmount),
                g.Sum(x => x.item.Discount),
                g.Sum(x => x.item.NetAmount - x.item.Vat),
                g.Sum(x => x.item.UnitCost * x.item.Quantity)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<Guid> ids = [.. grouped.Select(g => g.Id)];

        var names = await context.Set<AppUser>()
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.EmployeeCode, u.DisplayName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Named(
            grouped,
            names.ToDictionary(n => n.Id, n => (n.EmployeeCode ?? string.Empty, n.DisplayName)));
    }

    /// <summary>
    /// Attaches names to grouped rows, keeping a row whose name has gone.
    /// </summary>
    /// <remarks>
    /// A cashier deleted from the directory, or a store removed, still sold what
    /// they sold. Dropping the row would quietly make the totals disagree with the
    /// takings, so the row stands with its identifier and no name.
    /// </remarks>
    private static List<Aggregate> Named(
        List<KeyRow> rows,
        Dictionary<Guid, (string Code, string Name)> names)
        =>
        [
            .. rows.Select(r =>
            {
                (string code, string name) = names.TryGetValue(r.Id, out (string Code, string Name) found)
                    ? found
                    : (string.Empty, string.Empty);

                return new Aggregate(
                    r.Id, code, name, r.SaleCount, r.QuantitySold, r.QuantityReturned,
                    r.GrossRevenue, r.Discount, r.Revenue, r.Cost);
            }),
        ];

    private static Aggregate Fold(Guid id, string code, string name, IEnumerable<Aggregate> parts)
    {
        List<Aggregate> all = [.. parts];

        return new Aggregate(
            id,
            code,
            name,
            all.Sum(a => a.SaleCount),
            all.Sum(a => a.QuantitySold),
            all.Sum(a => a.QuantityReturned),
            all.Sum(a => a.GrossRevenue),
            all.Sum(a => a.Discount),
            all.Sum(a => a.Revenue),
            all.Sum(a => a.Cost));
    }

    /// <summary>
    /// Turns one aggregate into a row, doing the division the database should not.
    /// </summary>
    private static SalesAnalysisRow Row(Aggregate a)
    {
        decimal profit = a.Revenue - a.Cost;

        // Nothing earned is a margin of nothing, not a division by zero and not a
        // null the caller has to special-case. Giving stock away is a real thing a
        // shop does, and the report has to survive it.
        decimal margin = a.Revenue == 0m
            ? 0m
            : decimal.Round(profit / a.Revenue * 100m, 2, MidpointRounding.AwayFromZero);

        return new SalesAnalysisRow(
            a.Id,
            a.Code,
            a.Name,
            a.SaleCount,
            a.QuantitySold,
            a.QuantityReturned,
            a.GrossRevenue,
            a.Discount,
            a.Revenue,
            a.Cost,
            profit,
            margin);
    }

    private sealed record Aggregate(
        Guid Id,
        string Code,
        string Name,
        int SaleCount,
        decimal QuantitySold,
        decimal QuantityReturned,
        decimal GrossRevenue,
        decimal Discount,
        decimal Revenue,
        decimal Cost);

    private sealed record KeyRow(
        Guid Id,
        int SaleCount,
        decimal QuantitySold,
        decimal QuantityReturned,
        decimal GrossRevenue,
        decimal Discount,
        decimal Revenue,
        decimal Cost);

    private sealed record ProductRow(
        ProductId ProductId,
        string ProductName,
        int SaleCount,
        decimal QuantitySold,
        decimal QuantityReturned,
        decimal GrossRevenue,
        decimal Discount,
        decimal Revenue,
        decimal Cost);
}
