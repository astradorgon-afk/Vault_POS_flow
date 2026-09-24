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
/// Read-only views that let the owner monitor each store from head office:
/// what every store sold and took in over a period, and what each store holds,
/// from the scarcest item to the most plentiful. Selling itself happens at the
/// store registers, which sync their documents here.
/// </summary>
public static class StoreMonitoringEndpoints
{
    /// <summary>How many days of sales the stock view averages to estimate how long stock lasts.</summary>
    private const int SalesRateDays = 14;

    /// <summary>The longest period a performance request may cover.</summary>
    private const int MaxPeriodDays = 366;

    public static IEndpointRouteBuilder MapStoreMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/dashboard/store-performance", GetStorePerformanceAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.View) { Scope = ScopeSource.None })
            .WithTags("Dashboard")
            .WithName("GetStorePerformance")
            .WithSummary("Gets each store's sales, takings and transactions over a period of business dates.");

        app.MapGet("/api/v1/inventory/stock-levels", GetStockLevelsAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.View) { Scope = ScopeSource.None })
            .WithTags("Inventory")
            .WithName("GetStockLevels")
            .WithSummary("Gets a location's stock per product with its thresholds and how long it is expected to last.");

        return app;
    }

    private static async Task<IResult> GetStorePerformanceAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        ISystemClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        DateOnly end = to ?? clock.BusinessDateFor("Asia/Manila");
        DateOnly start = from ?? end.AddDays(-6);
        if (start > end || end.DayNumber - start.DayNumber >= MaxPeriodDays)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.Validation(
                    "store_performance.period_invalid",
                    FormattableString.Invariant($"Choose a period of 1 to {MaxPeriodDays} days that ends on or after it starts."))),
                currentUser.CorrelationId.Value);
        }

        List<Location> stores = await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind == LocationKind.Store)
            .OrderBy(l => l.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        stores = [.. stores.Where(s => authorization.HasAllLocations || authorization.Locations.Contains(s.Id))];
        List<LocationId> storeIds = [.. stores.Select(s => s.Id)];

        List<Sale> sales = await context.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .AsSplitQuery()
            .Where(s => storeIds.Contains(s.LocationId) && s.BusinessDate >= start && s.BusinessDate <= end)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<SalesReturn> returns = await context.SalesReturns
            .AsNoTracking()
            .Include(r => r.Refunds)
            .Where(r => storeIds.Contains(r.LocationId) && r.BusinessDate >= start && r.BusinessDate <= end)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<StorePerformance> performance = [];
        foreach (Location store in stores)
        {
            List<Sale> storeSales = [.. sales.Where(s => s.LocationId == store.Id)];
            List<Sale> completed = [.. storeSales.Where(s => s.Status == SaleStatus.Completed)];
            List<Sale> voided = [.. storeSales.Where(s => s.Status == SaleStatus.Voided)];
            decimal refunded = returns
                .Where(r => r.LocationId == store.Id)
                .SelectMany(r => r.Refunds)
                .Sum(f => f.Amount);
            decimal net = completed.Sum(s => s.NetTotal);

            List<StoreDailySales> daily = [];
            for (DateOnly day = start; day <= end; day = day.AddDays(1))
            {
                List<Sale> onDay = [.. completed.Where(s => s.BusinessDate == day)];
                daily.Add(new StoreDailySales(day, onDay.Sum(s => s.NetTotal), onDay.Count));
            }

            List<StoreTopProduct> topProducts = [.. completed
                .SelectMany(s => s.Items)
                .GroupBy(i => (i.ProductId, i.ProductName))
                .Select(g => new StoreTopProduct(
                    g.Key.ProductId.Value, g.Key.ProductName, g.Sum(i => i.Quantity), g.Sum(i => i.NetAmount)))
                .OrderByDescending(p => p.NetSales)
                .Take(5)];

            performance.Add(new StorePerformance(
                store.Id.Value,
                store.Code,
                store.Name,
                net,
                completed.Sum(s => s.GrossTotal),
                completed.Sum(s => s.DiscountTotal),
                completed.Count,
                completed.Count == 0 ? 0m : decimal.Round(net / completed.Count, 2),
                voided.Count,
                voided.Sum(s => s.NetTotal),
                refunded,
                net - refunded,
                PaymentTotal(completed, PaymentMethod.Cash),
                PaymentTotal(completed, PaymentMethod.Card),
                PaymentTotal(completed, PaymentMethod.EWallet),
                storeSales.Count == 0 ? null : storeSales.Max(s => s.CompletedAtUtc),
                daily,
                topProducts));
        }

        return TypedResults.Ok(new StorePerformanceReport(start, end, performance));
    }

    private static decimal PaymentTotal(IEnumerable<Sale> sales, PaymentMethod method)
        => sales.SelectMany(s => s.Payments).Where(p => p.Method == method).Sum(p => p.Amount);

    private static async Task<IResult> GetStockLevelsAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        ISystemClock clock,
        [FromQuery] Guid locationId,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        LocationId scope = new(locationId);
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == scope && l.Kind != LocationKind.External, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(Error.NotFound("stock_levels.location_unknown", "That location does not exist.")),
                currentUser.CorrelationId.Value);
        }

        if (!authorization.HasAllLocations && !authorization.Locations.Contains(scope))
        {
            return TypedResults.Forbid();
        }

        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == scope)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ProductId, ProductLocationSetting> settings = await context.ProductLocationSettings
            .AsNoTracking()
            .Where(s => s.LocationId == scope)
            .ToDictionaryAsync(s => s.ProductId, cancellationToken)
            .ConfigureAwait(false);

        List<Product> products = await context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<CategoryId, string> categories = await context.Categories
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken)
            .ConfigureAwait(false);

        // The recent sales rate turns a quantity into "how long will this last".
        DateOnly today = clock.BusinessDateFor(location.TimeZoneId);
        DateOnly rateStart = today.AddDays(-(SalesRateDays - 1));
        Dictionary<ProductId, decimal> soldRecently = await context.SaleItems
            .AsNoTracking()
            .Join(
                context.Sales.Where(s => s.LocationId == scope
                    && s.Status == SaleStatus.Completed
                    && s.BusinessDate >= rateStart
                    && s.BusinessDate <= today),
                item => item.SaleId,
                sale => sale.Id,
                (item, _) => new { item.ProductId, item.Quantity })
            .GroupBy(x => x.ProductId)
            .Select(g => new { ProductId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Quantity, cancellationToken)
            .ConfigureAwait(false);

        List<StockLevelRow> rows = [];
        foreach (Product product in products)
        {
            List<InventoryBalance> held = [.. balances.Where(b => b.ProductId == product.Id)];
            settings.TryGetValue(product.Id, out ProductLocationSetting? setting);

            // A product the location neither stocks nor holds is not part of its inventory.
            if (held.Count == 0 && setting is not { IsStocked: true })
            {
                continue;
            }

            decimal available = held.Where(b => b.State == InventoryState.Available).Sum(b => b.Quantity);
            decimal inTransit = held.Where(b => b.State is InventoryState.InTransit or InventoryState.TransitVariance).Sum(b => b.Quantity);
            decimal onHold = held
                .Where(b => b.State is InventoryState.Quarantine or InventoryState.Damaged or InventoryState.Expired
                    or InventoryState.PendingInspection or InventoryState.ReturnPending)
                .Sum(b => b.Quantity);
            decimal value = held.Where(b => b.State == InventoryState.Available).Sum(b => b.TotalValue);

            decimal dailyRate = decimal.Round(soldRecently.GetValueOrDefault(product.Id) / SalesRateDays, 2);
            decimal? daysOfCover = dailyRate > 0m ? decimal.Round(Math.Max(available, 0m) / dailyRate, 1) : null;

            rows.Add(new StockLevelRow(
                product.Id.Value,
                product.Sku.Value,
                product.Name,
                categories.GetValueOrDefault(product.CategoryId, "Uncategorized"),
                available,
                inTransit,
                onHold,
                value,
                setting?.MinimumStock,
                setting?.ReorderPoint,
                setting?.TargetStock,
                setting?.MaximumStock,
                dailyRate,
                daysOfCover,
                Classify(available, setting)));
        }

        return TypedResults.Ok(new StockLevelReport(
            location.Id.Value,
            location.Code,
            location.Name,
            SalesRateDays,
            [.. rows.OrderBy(r => StatusRank(r.Status)).ThenBy(r => r.Available)]));
    }

    /// <summary>Out (nothing left), Low (at or under the reorder point), Healthy,
    /// or Over (above the maximum); without thresholds only Out and Healthy apply.</summary>
    private static string Classify(decimal available, ProductLocationSetting? setting)
    {
        if (available <= 0m)
        {
            return "Out";
        }

        if (setting is null)
        {
            return "Healthy";
        }

        if (available <= setting.ReorderPoint)
        {
            return "Low";
        }

        return setting.MaximumStock > 0m && available > setting.MaximumStock ? "Over" : "Healthy";
    }

    private static int StatusRank(string status) => status switch
    {
        "Out" => 0,
        "Low" => 1,
        "Healthy" => 2,
        _ => 3,
    };
}

/// <summary>Every store's performance over one period of business dates.</summary>
public sealed record StorePerformanceReport(DateOnly From, DateOnly To, IReadOnlyList<StorePerformance> Stores);

/// <summary>
/// What one store sold and took in over the period. Net sales are completed
/// sales after discounts (VAT inclusive); takings are net sales less refunds
/// paid out, the money the store kept.
/// </summary>
public sealed record StorePerformance(
    Guid LocationId,
    string Code,
    string Name,
    decimal NetSales,
    decimal GrossSales,
    decimal Discounts,
    int Transactions,
    decimal AverageTicket,
    int VoidedTransactions,
    decimal VoidedValue,
    decimal Refunds,
    decimal Takings,
    decimal CashSales,
    decimal CardSales,
    decimal EWalletSales,
    DateTimeOffset? LastSaleAtUtc,
    IReadOnlyList<StoreDailySales> Daily,
    IReadOnlyList<StoreTopProduct> TopProducts);

/// <summary>One business date's completed sales at a store.</summary>
public sealed record StoreDailySales(DateOnly Date, decimal NetSales, int Transactions);

/// <summary>One of a store's best sellers over the period.</summary>
public sealed record StoreTopProduct(Guid ProductId, string Name, decimal Quantity, decimal NetSales);

/// <summary>A location's stock per product; the daily sales rate averages the
/// last <c>SalesRateDays</c> days.</summary>
public sealed record StockLevelReport(
    Guid LocationId,
    string Code,
    string Name,
    int SalesRateDays,
    IReadOnlyList<StockLevelRow> Products);

/// <summary>
/// One product's stock at a location: the sellable quantity, what is in transit
/// or held back (quarantine, damaged, expired, awaiting inspection), the cost
/// value of the sellable stock, the average units sold per day, how many days
/// the stock lasts at that rate (null when it is not selling), and a status of
/// Out, Low, Healthy or Over.
/// </summary>
public sealed record StockLevelRow(
    Guid ProductId,
    string Sku,
    string Name,
    string Category,
    decimal Available,
    decimal InTransit,
    decimal OnHold,
    decimal StockValue,
    decimal? MinimumStock,
    decimal? ReorderPoint,
    decimal? TargetStock,
    decimal? MaximumStock,
    decimal DailySales,
    decimal? DaysOfCover,
    string Status);
