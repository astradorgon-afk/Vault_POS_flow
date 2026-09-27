using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>
/// The owner's product analytics: what sells best and worst in each store over
/// a period, and across the business.
/// </summary>
public static class ProductAnalyticsEndpoints
{
    private const int MaxPeriodDays = 366;
    private const int DefaultLimit = 5;
    private const int MaxLimit = 25;

    public static IEndpointRouteBuilder MapProductAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/dashboard/product-performance", GetProductPerformanceAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.View) { Scope = ScopeSource.None })
            .WithTags("Dashboard")
            .WithName("GetProductPerformance")
            .WithSummary("Gets each store's best- and worst-selling products over a period, and the business's overall.");

        return app;
    }

    private static async Task<IResult> GetProductPerformanceAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        ISystemClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? rankBy,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        DateOnly end = to ?? clock.BusinessDateFor("Asia/Manila");
        DateOnly start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.Validation(
                    "product_performance.period_invalid",
                    FormattableString.Invariant($"Choose a period of 1 to {MaxPeriodDays} days that ends on or after it starts."))),
                currentUser.CorrelationId.Value);
        }

        bool byUnits = string.Equals(rankBy, "units", StringComparison.OrdinalIgnoreCase);
        int take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        List<Location> stores = await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind == LocationKind.Store)
            .OrderBy(l => l.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        stores = [.. stores.Where(s => authorization.HasAllLocations || authorization.Locations.Contains(s.Id))];
        List<LocationId> storeIds = [.. stores.Select(s => s.Id)];

        var sold = await StoreMonitoringEndpoints.AggregateAsync(
            context,
            context.SaleItems.AsNoTracking().Join(
                context.Sales.Where(s => storeIds.Contains(s.LocationId)
                    && s.Status == SaleStatus.Completed
                    && s.BusinessDate >= start
                    && s.BusinessDate <= end),
                i => i.SaleId,
                s => s.Id,
                (i, s) => new { s.LocationId, i.ProductId, i.Quantity, i.NetAmount }),
            rows => rows
                .GroupBy(i => new { i.LocationId, i.ProductId })
                .Select(g => new { g.Key.LocationId, g.Key.ProductId, Quantity = g.Sum(i => i.Quantity), Net = g.Sum(i => i.NetAmount) }),
            cancellationToken).ConfigureAwait(false);

        var onHand = await StoreMonitoringEndpoints.AggregateAsync(
            context,
            context.InventoryBalances.AsNoTracking()
                .Where(b => storeIds.Contains(b.LocationId) && b.State == InventoryState.Available)
                .Select(b => new { b.LocationId, b.ProductId, b.Quantity }),
            rows => rows
                .GroupBy(b => new { b.LocationId, b.ProductId })
                .Select(g => new { g.Key.LocationId, g.Key.ProductId, Quantity = g.Sum(b => b.Quantity) }),
            cancellationToken).ConfigureAwait(false);

        List<(LocationId Location, ProductId Product)> stocked = [.. (await context.ProductLocationSettings
            .AsNoTracking()
            .Where(s => s.IsStocked && storeIds.Contains(s.LocationId))
            .Select(s => new { s.LocationId, s.ProductId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(s => (s.LocationId, s.ProductId))];

        Dictionary<ProductId, Product> products = await context.Products
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<CategoryId, string> categories = await context.Categories
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(LocationId, ProductId), (decimal Quantity, decimal Net)> soldBy = sold.ToDictionary(
            s => (s.LocationId, s.ProductId), s => (s.Quantity, s.Net));
        Dictionary<(LocationId, ProductId), decimal> onHandBy = onHand.ToDictionary(
            b => (b.LocationId, b.ProductId), b => b.Quantity);

        List<StoreProductPerformance> result = [];
        foreach (Location store in stores)
        {
            // A store's range: what it is set up to stock, what it holds, and
            // anything it sold; only active products can be the worst sellers.
            HashSet<ProductId> range = [
                .. stocked.Where(s => s.Location == store.Id).Select(s => s.Product),
                .. onHandBy.Where(b => b.Key.Item1 == store.Id && b.Value > 0).Select(b => b.Key.Item2),
                .. soldBy.Keys.Where(k => k.Item1 == store.Id).Select(k => k.Item2),
            ];

            List<ProductSalesLine> lines = [.. range
                .Where(products.ContainsKey)
                .Select(id => LineFor(
                    products[id],
                    categories,
                    soldBy.GetValueOrDefault((store.Id, id)).Quantity,
                    soldBy.GetValueOrDefault((store.Id, id)).Net,
                    onHandBy.TryGetValue((store.Id, id), out decimal held) ? held : 0m))];

            result.Add(ProductRanking.Summarise(store.Id.Value, store.Code, store.Name, lines, byUnits, take));
        }

        // Across the business: the same products summed over every store shown.
        List<ProductSalesLine> business = [.. result.Count == 0
            ? []
            : soldBy.Keys.Select(k => k.Item2)
                .Concat(stocked.Select(s => s.Product))
                .Concat(onHandBy.Where(b => b.Value > 0).Select(b => b.Key.Item2))
                .Distinct()
                .Where(products.ContainsKey)
                .Select(id => LineFor(
                    products[id],
                    categories,
                    soldBy.Where(s => s.Key.Item2 == id).Sum(s => s.Value.Quantity),
                    soldBy.Where(s => s.Key.Item2 == id).Sum(s => s.Value.Net),
                    onHandBy.Where(b => b.Key.Item2 == id).Sum(b => b.Value)))];

        return TypedResults.Ok(new ProductPerformanceReport(
            start,
            end,
            byUnits ? "units" : "revenue",
            ProductRanking.Summarise(Guid.Empty, "ALL", "All stores", business, byUnits, take),
            result));
    }

    private static ProductSalesLine LineFor(
        Product product, Dictionary<CategoryId, string> categories, decimal quantity, decimal net, decimal onHand)
        => new(
            product.Id.Value,
            product.Sku.Value,
            product.Name,
            categories.GetValueOrDefault(product.CategoryId, "Uncategorized"),
            product.IsActive,
            quantity,
            net,
            onHand);
}

/// <summary>One product's sales and stock at a store (or across stores) over a period.</summary>
public sealed record ProductSalesLine(
    Guid ProductId,
    string Sku,
    string Name,
    string Category,
    bool IsActive,
    decimal Quantity,
    decimal Net,
    decimal OnHand);

/// <summary>The rules that rank products as best and worst sellers.</summary>
public static class ProductRanking
{
    /// <summary>Ranks one store's (or the business's) products.</summary>
    /// <param name="id">The store, or empty for the business.</param>
    /// <param name="code">Its code.</param>
    /// <param name="name">Its name.</param>
    /// <param name="lines">Every product in its range, with what it sold.</param>
    /// <param name="byUnits">True to rank by units sold, false by revenue.</param>
    /// <param name="take">How many best and worst to return.</param>
    /// <returns>The ranking.</returns>
    public static StoreProductPerformance Summarise(
        Guid id, string code, string name, IReadOnlyList<ProductSalesLine> lines, bool byUnits, int take)
    {
        ArgumentNullException.ThrowIfNull(lines);
        decimal totalNet = lines.Sum(l => l.Net);
        decimal totalUnits = lines.Sum(l => l.Quantity);

        ProductRank Rank(ProductSalesLine line, int position) => new(
            position,
            line.ProductId,
            line.Sku,
            line.Name,
            line.Category,
            line.Quantity,
            line.Net,
            byUnits
                ? (totalUnits == 0 ? 0m : decimal.Round(line.Quantity / totalUnits * 100m, 1))
                : (totalNet == 0 ? 0m : decimal.Round(line.Net / totalNet * 100m, 1)),
            line.OnHand);

        List<ProductSalesLine> selling = [.. lines.Where(l => l.Quantity > 0)];
        List<ProductRank> best = [.. selling
            .OrderByDescending(l => byUnits ? l.Quantity : l.Net)
            .ThenByDescending(l => byUnits ? l.Net : l.Quantity)
            .Take(take)
            .Select((l, i) => Rank(l, i + 1))];

        // Worst: nothing sold comes first, the most stock first (that is the
        // money standing still), then the slowest of those that did sell.
        // Discontinued products are no longer the store's to sell.
        List<ProductRank> worst = [.. lines
            .Where(l => l.IsActive)
            .OrderBy(l => l.Quantity > 0 ? 1 : 0)
            .ThenBy(l => l.Quantity > 0 ? (byUnits ? l.Quantity : l.Net) : -l.OnHand)
            .ThenBy(l => l.Name, StringComparer.CurrentCulture)
            .Take(take)
            .Select((l, i) => Rank(l, i + 1))];

        return new StoreProductPerformance(
            id,
            code,
            name,
            totalNet,
            totalUnits,
            selling.Count,
            lines.Count(l => l.Quantity == 0 && l.IsActive),
            best,
            worst);
    }
}

/// <summary>Best- and worst-selling products over a period.</summary>
/// <param name="From">The first business date.</param>
/// <param name="To">The last business date.</param>
/// <param name="RankBy">What products are ranked by: revenue or units.</param>
/// <param name="Business">Every store shown, taken together.</param>
/// <param name="Stores">Each store the caller may see.</param>
public sealed record ProductPerformanceReport(
    DateOnly From,
    DateOnly To,
    string RankBy,
    StoreProductPerformance Business,
    IReadOnlyList<StoreProductPerformance> Stores);

/// <summary>One store's best and worst sellers.</summary>
/// <param name="LocationId">The store, or empty for the whole business.</param>
/// <param name="Code">Its code.</param>
/// <param name="Name">Its name.</param>
/// <param name="NetSales">What its products sold for, net, over the period.</param>
/// <param name="UnitsSold">How many units it sold.</param>
/// <param name="ProductsSold">How many different products sold at least once.</param>
/// <param name="ProductsNotSold">How many products in its range sold nothing.</param>
/// <param name="Best">The best sellers, best first.</param>
/// <param name="Worst">The worst sellers, worst first.</param>
public sealed record StoreProductPerformance(
    Guid LocationId,
    string Code,
    string Name,
    decimal NetSales,
    decimal UnitsSold,
    int ProductsSold,
    int ProductsNotSold,
    IReadOnlyList<ProductRank> Best,
    IReadOnlyList<ProductRank> Worst);

/// <summary>One product's place in a ranking.</summary>
/// <param name="Rank">1 for the best (or worst).</param>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="Name">Its name.</param>
/// <param name="Category">Its category.</param>
/// <param name="Quantity">Units sold in the period.</param>
/// <param name="NetSales">What those units sold for, net.</param>
/// <param name="Share">Its percentage of the store's revenue (or units, when ranked by units).</param>
/// <param name="OnHand">Sellable stock at the store now.</param>
public sealed record ProductRank(
    int Rank,
    Guid ProductId,
    string Sku,
    string Name,
    string Category,
    decimal Quantity,
    decimal NetSales,
    decimal Share,
    decimal OnHand);
