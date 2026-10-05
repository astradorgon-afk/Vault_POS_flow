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
using Pos.Domain.Transfers;
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
    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    private sealed class StoreMovementAccumulator(Location location, DateOnly periodStart, int periodDays)
    {
        public Location Location { get; } = location;
        public int PeriodDays { get; } = periodDays;
        public DateOnly PeriodStart { get; } = periodStart;
        public decimal IncomingUnits { get; set; }
        public int InboundTransfers { get; set; }
        public decimal OutgoingUnits { get; set; }
        public int OutboundTransfers { get; set; }
        public int RestockRequests { get; set; }
        public int PendingApprovalRequests { get; set; }
        public int AwaitingDispatchRequests { get; set; }
        public DateTimeOffset? LastIncomingAtUtc { get; set; }
        public DateTimeOffset? LastOutgoingAtUtc { get; set; }
        public List<StoreMovementItem> RecentItems { get; } = [];
        public decimal[] DailyIncoming { get; } = new decimal[7];
        public decimal[] DailyOutgoing { get; } = new decimal[7];

        public void AddItems(IEnumerable<StoreMovementItem> items) => RecentItems.AddRange(items);

        public void AddDailyUnits(DateTimeOffset occurredAtUtc, decimal units, bool incoming)
        {
            DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurredAtUtc, StoreTimeZone).DateTime);
            int dayOffset = Math.Clamp(date.DayNumber - PeriodStart.DayNumber, 0, PeriodDays - 1);
            int bucket = Math.Min(6, dayOffset * 7 / PeriodDays);
            if (incoming)
            {
                DailyIncoming[bucket] += units;
            }
            else
            {
                DailyOutgoing[bucket] += units;
            }
        }
    }

    public static IEndpointRouteBuilder MapStoreMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/dashboard/store-performance", GetStorePerformanceAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Sales.View) { Scope = ScopeSource.None })
            .WithTags("Dashboard")
            .WithName("GetStorePerformance")
            .WithSummary("Gets each store's sales, takings and transactions over a period of business dates.");

        app.MapGet("/api/v1/dashboard/store-movements", GetStoreMovementsAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.View) { Scope = ScopeSource.None })
            .WithTags("Dashboard")
            .WithName("GetStoreMovements")
            .WithSummary("Gets dispatched product movements and store restock requests over a period.");

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

        // PostgreSQL sums everything itself: a busy chain rings up thousands of
        // sales a week and the report must count every one of them. SQLite keeps
        // money and instants as text, so there the same query runs over the rows.
        IQueryable<Sale> inPeriod = context.Sales
            .AsNoTracking()
            .Where(s => storeIds.Contains(s.LocationId) && s.BusinessDate >= start && s.BusinessDate <= end);

        var days = await AggregateAsync(
            context,
            inPeriod.Select(s => new { s.LocationId, s.BusinessDate, s.Status, s.NetTotal, s.GrossTotal, s.DiscountTotal, s.CompletedAtUtc }),
            rows => rows
                .GroupBy(s => new { s.LocationId, s.BusinessDate, s.Status })
                .Select(g => new
                {
                    g.Key.LocationId,
                    g.Key.BusinessDate,
                    g.Key.Status,
                    Count = g.Count(),
                    Net = g.Sum(s => s.NetTotal),
                    Gross = g.Sum(s => s.GrossTotal),
                    Discount = g.Sum(s => s.DiscountTotal),
                    LastAt = g.Max(s => s.CompletedAtUtc),
                }),
            cancellationToken).ConfigureAwait(false);

        var payments = await AggregateAsync(
            context,
            context.Payments.AsNoTracking().Join(
                inPeriod.Where(s => s.Status == SaleStatus.Completed),
                p => EF.Property<SaleId>(p, "SaleId"),
                s => s.Id,
                (p, s) => new { s.LocationId, p.Method, p.Amount }),
            rows => rows
                .GroupBy(p => new { p.LocationId, p.Method })
                .Select(g => new { g.Key.LocationId, g.Key.Method, Amount = g.Sum(p => p.Amount) }),
            cancellationToken).ConfigureAwait(false);

        var refunds = await AggregateAsync(
            context,
            context.Refunds.AsNoTracking().Join(
                context.SalesReturns.Where(r => storeIds.Contains(r.LocationId) && r.BusinessDate >= start && r.BusinessDate <= end),
                f => f.SalesReturnId,
                r => r.Id,
                (f, r) => new { r.LocationId, f.Amount }),
            rows => rows
                .GroupBy(f => f.LocationId)
                .Select(g => new { LocationId = g.Key, Amount = g.Sum(f => f.Amount) }),
            cancellationToken).ConfigureAwait(false);

        var products = await AggregateAsync(
            context,
            context.SaleItems.AsNoTracking().Join(
                inPeriod.Where(s => s.Status == SaleStatus.Completed),
                i => i.SaleId,
                s => s.Id,
                (i, s) => new { s.LocationId, i.ProductId, i.ProductName, i.Quantity, i.NetAmount }),
            rows => rows
                .GroupBy(i => new { i.LocationId, i.ProductId })
                .Select(g => new
                {
                    g.Key.LocationId,
                    g.Key.ProductId,
                    Name = g.Max(i => i.ProductName),
                    Quantity = g.Sum(i => i.Quantity),
                    Net = g.Sum(i => i.NetAmount),
                }),
            cancellationToken).ConfigureAwait(false);

        List<StorePerformance> performance = [];
        foreach (Location store in stores)
        {
            var storeDays = days.Where(d => d.LocationId == store.Id).ToList();
            var completed = storeDays.Where(d => d.Status == SaleStatus.Completed).ToList();
            var voided = storeDays.Where(d => d.Status == SaleStatus.Voided).ToList();
            decimal refunded = refunds.Where(r => r.LocationId == store.Id).Sum(r => r.Amount);
            decimal net = completed.Sum(d => d.Net);
            int transactions = completed.Sum(d => d.Count);

            List<StoreDailySales> daily = [];
            for (DateOnly day = start; day <= end; day = day.AddDays(1))
            {
                var onDay = completed.Where(d => d.BusinessDate == day).ToList();
                daily.Add(new StoreDailySales(day, onDay.Sum(d => d.Net), onDay.Sum(d => d.Count), onDay.Sum(d => d.Gross)));
            }

            List<StoreTopProduct> topProducts = [.. products
                .Where(p => p.LocationId == store.Id)
                .OrderByDescending(p => p.Net)
                .Take(5)
                .Select(p => new StoreTopProduct(p.ProductId.Value, p.Name ?? string.Empty, p.Quantity, p.Net))];

            decimal PaymentTotal(PaymentMethod method)
                => payments.Where(p => p.LocationId == store.Id && p.Method == method).Sum(p => p.Amount);

            performance.Add(new StorePerformance(
                store.Id.Value,
                store.Code,
                store.Name,
                net,
                completed.Sum(d => d.Gross),
                completed.Sum(d => d.Discount),
                transactions,
                transactions == 0 ? 0m : decimal.Round(net / transactions, 2),
                voided.Sum(d => d.Count),
                voided.Sum(d => d.Net),
                refunded,
                net - refunded,
                PaymentTotal(PaymentMethod.Cash),
                PaymentTotal(PaymentMethod.Card),
                PaymentTotal(PaymentMethod.EWallet),
                storeDays.Count == 0 ? null : storeDays.Max(d => d.LastAt),
                daily,
                topProducts));
        }

        return TypedResults.Ok(new StorePerformanceReport(start, end, performance));
    }

    private static async Task<IResult> GetStoreMovementsAsync(
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
                    "store_movements.period_invalid",
                    FormattableString.Invariant($"Choose a period of 1 to {MaxPeriodDays} days that ends on or after it starts."))),
                currentUser.CorrelationId.Value);
        }

        DateTimeOffset startUtc = StartOfStoreDayUtc(start);
        DateTimeOffset endUtc = StartOfStoreDayUtc(end.AddDays(1));

        List<Location> stores = await context.Locations
            .AsNoTracking()
            .Where(location => location.Kind == LocationKind.Store)
            .OrderBy(location => location.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        stores = [.. stores.Where(store => authorization.HasAllLocations || authorization.Locations.Contains(store.Id))];
        Dictionary<Guid, StoreMovementAccumulator> movements = stores.ToDictionary(
            store => store.Id.Value,
            store => new StoreMovementAccumulator(store, start, end.DayNumber - start.DayNumber + 1));

        List<Transfer> transfers = [];
        if (movements.Count > 0)
        {
            LocationId[] storeIds = [.. stores.Select(store => store.Id)];
            transfers = await context.Transfers
                .AsNoTracking()
                .Where(transfer => storeIds.Contains(transfer.SourceLocationId) || storeIds.Contains(transfer.DestinationLocationId))
                .Where(transfer =>
                    (transfer.Mode == TransferMode.StoreRestock && transfer.CreatedAtUtc >= startUtc && transfer.CreatedAtUtc < endUtc)
                    || (transfer.DispatchedAtUtc != null && transfer.DispatchedAtUtc >= startUtc && transfer.DispatchedAtUtc < endUtc))
                .Include(transfer => transfer.Lines)
                .Include(transfer => transfer.Allocations)
                .AsSplitQuery()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        HashSet<Guid> routeLocationIds = [.. transfers
            .SelectMany(transfer => new[] { transfer.SourceLocationId.Value, transfer.DestinationLocationId.Value })
            .Distinct()];
        List<Location> routeLocations = routeLocationIds.Count == 0
            ? []
            : await context.Locations
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        Dictionary<Guid, string> locationNames = routeLocations
            .Where(location => routeLocationIds.Contains(location.Id.Value))
            .ToDictionary(location => location.Id.Value, location => location.Name);

        ProductId[] productIds = [.. transfers
            .SelectMany(transfer => transfer.Lines)
            .Select(line => line.ProductId)
            .Distinct()];
        List<Product> products = productIds.Length == 0
            ? []
            : await context.Products
                .AsNoTracking()
                .Where(product => productIds.Contains(product.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        Dictionary<Guid, string> productNames = products
            .ToDictionary(product => product.Id.Value, product => product.Name);

        List<StoreMovementActivity> activity = [];
        int dispatchedTransferCount = 0;
        foreach (Transfer transfer in transfers)
        {
            bool requestInPeriod = transfer.Mode == TransferMode.StoreRestock
                && transfer.CreatedAtUtc >= startUtc
                && transfer.CreatedAtUtc < endUtc;
            bool dispatchInPeriod = transfer.Status != TransferStatus.Cancelled
                && transfer.DispatchedAtUtc is { } dispatchedAt
                && dispatchedAt >= startUtc
                && dispatchedAt < endUtc;
            decimal requestedUnits = transfer.Lines.Sum(line => line.RequestedQuantity);
            decimal dispatchedUnits = transfer.Allocations.Sum(allocation => allocation.Quantity);
            Guid sourceId = transfer.SourceLocationId.Value;
            Guid destinationId = transfer.DestinationLocationId.Value;
            string reference = string.IsNullOrWhiteSpace(transfer.Number)
                ? $"REQ-{transfer.Id.Value.ToString("N")[..8].ToUpperInvariant()}"
                : transfer.Number;

            if (requestInPeriod && movements.TryGetValue(destinationId, out StoreMovementAccumulator? requestStore))
            {
                requestStore.RestockRequests++;
                if (transfer.Status is TransferStatus.Submitted or TransferStatus.InReview)
                {
                    requestStore.PendingApprovalRequests++;
                }
                else if (transfer.Status is TransferStatus.Approved or TransferStatus.Picking or TransferStatus.Ready)
                {
                    requestStore.AwaitingDispatchRequests++;
                }

                requestStore.AddItems(transfer.Lines.Select(line => new StoreMovementItem(
                    productNames.GetValueOrDefault(line.ProductId.Value) ?? "Unknown product",
                    reference,
                    "Requested",
                    line.RequestedQuantity,
                    transfer.CreatedAtUtc)));

                activity.Add(new StoreMovementActivity(
                    transfer.Id.Value,
                    reference,
                    "Store request",
                    transfer.Status.ToString(),
                    locationNames.GetValueOrDefault(sourceId) ?? sourceId.ToString("N")[..8].ToUpperInvariant(),
                    locationNames.GetValueOrDefault(destinationId) ?? destinationId.ToString("N")[..8].ToUpperInvariant(),
                    requestedUnits,
                    transfer.CreatedAtUtc));
            }

            if (!dispatchInPeriod)
            {
                continue;
            }

            DateTimeOffset actualDispatchedAt = transfer.DispatchedAtUtc!.Value;
            dispatchedTransferCount++;
            if (movements.TryGetValue(sourceId, out StoreMovementAccumulator? sourceStore))
            {
                sourceStore.OutgoingUnits += dispatchedUnits;
                sourceStore.OutboundTransfers++;
                sourceStore.LastOutgoingAtUtc = Latest(sourceStore.LastOutgoingAtUtc, actualDispatchedAt);
                sourceStore.AddDailyUnits(actualDispatchedAt, dispatchedUnits, incoming: false);
                sourceStore.AddItems(DispatchedItems(transfer, productNames, reference, "Sent"));
            }

            if (movements.TryGetValue(destinationId, out StoreMovementAccumulator? destinationStore))
            {
                destinationStore.IncomingUnits += dispatchedUnits;
                destinationStore.InboundTransfers++;
                destinationStore.LastIncomingAtUtc = Latest(destinationStore.LastIncomingAtUtc, actualDispatchedAt);
                destinationStore.AddDailyUnits(actualDispatchedAt, dispatchedUnits, incoming: true);
                destinationStore.AddItems(DispatchedItems(transfer, productNames, reference, "Received"));
            }

            activity.Add(new StoreMovementActivity(
                transfer.Id.Value,
                reference,
                "Dispatched transfer",
                transfer.Status.ToString(),
                locationNames.GetValueOrDefault(sourceId) ?? sourceId.ToString("N")[..8].ToUpperInvariant(),
                locationNames.GetValueOrDefault(destinationId) ?? destinationId.ToString("N")[..8].ToUpperInvariant(),
                dispatchedUnits,
                actualDispatchedAt));
        }

        List<StoreMovement> storeResults = [.. movements.Values.Select(movement => new StoreMovement(
            movement.Location.Id.Value,
            movement.Location.Code,
            movement.Location.Name,
            movement.IncomingUnits,
            movement.InboundTransfers,
            movement.OutgoingUnits,
            movement.OutboundTransfers,
            movement.RestockRequests,
            movement.PendingApprovalRequests,
            movement.AwaitingDispatchRequests,
            movement.LastIncomingAtUtc,
            movement.LastOutgoingAtUtc,
            [.. movement.RecentItems
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(3)],
            [.. Enumerable.Range(0, 7).Select(index => new StoreMovementChartPoint(
                index,
                movement.DailyIncoming[index],
                movement.DailyOutgoing[index]))]))];

        return TypedResults.Ok(new StoreMovementReport(
            start,
            end,
            dispatchedTransferCount,
            storeResults,
            [.. activity.OrderByDescending(entry => entry.OccurredAtUtc).Take(12)]));
    }

    private static IEnumerable<StoreMovementItem> DispatchedItems(
        Transfer transfer,
        IReadOnlyDictionary<Guid, string> productNames,
        string reference,
        string direction)
    {
        Dictionary<int, decimal> pickedByLine = transfer.Allocations
            .GroupBy(allocation => allocation.LineNo)
            .ToDictionary(group => group.Key, group => group.Sum(allocation => allocation.Quantity));

        return transfer.Lines
            .Select(line => new StoreMovementItem(
                productNames.GetValueOrDefault(line.ProductId.Value) ?? "Unknown product",
                reference,
                direction,
                pickedByLine.GetValueOrDefault(line.LineNo),
                transfer.DispatchedAtUtc!.Value))
            .Where(item => item.Units > 0m);
    }

    private static DateTimeOffset StartOfStoreDayUtc(DateOnly date)
    {
        DateTime localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, StoreTimeZone));
    }

    private static DateTimeOffset? Latest(DateTimeOffset? current, DateTimeOffset? candidate)
        => candidate is { } value && (current is null || value > current.Value) ? value : current;

    /// <summary>
    /// Runs a grouping over <paramref name="rows"/> in the database, or, on
    /// SQLite (which stores money and instants as text), over the loaded rows.
    /// </summary>
    internal static async Task<List<TResult>> AggregateAsync<TRow, TResult>(
        PosDbContext context,
        IQueryable<TRow> rows,
        Func<IQueryable<TRow>, IQueryable<TResult>> aggregate,
        CancellationToken cancellationToken)
    {
        if (!context.IsSqlite)
        {
            return await aggregate(rows).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        List<TRow> loaded = await rows.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. aggregate(loaded.AsQueryable())];
    }

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

        // When each product last sold here, however long ago, so stock that has
        // stopped moving can be told apart from stock that is merely slow.
        Dictionary<ProductId, DateOnly> lastSold = await context.SaleItems
            .AsNoTracking()
            .Join(
                context.Sales.Where(s => s.LocationId == scope && s.Status == SaleStatus.Completed),
                item => item.SaleId,
                sale => sale.Id,
                (item, sale) => new { item.ProductId, sale.BusinessDate })
            .GroupBy(x => x.ProductId)
            .Select(g => new { ProductId = g.Key, Last = g.Max(x => x.BusinessDate) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Last, cancellationToken)
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
                Classify(available, setting),
                lastSold.TryGetValue(product.Id, out DateOnly sold) ? sold : null));
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
public sealed record StoreDailySales(DateOnly Date, decimal NetSales, int Transactions, decimal GrossSales);

/// <summary>One of a store's best sellers over the period.</summary>
public sealed record StoreTopProduct(Guid ProductId, string Name, decimal Quantity, decimal NetSales);

/// <summary>Every store's incoming and outgoing product movement over a date range.</summary>
public sealed record StoreMovementReport(
    DateOnly From,
    DateOnly To,
    int DispatchedTransferCount,
    IReadOnlyList<StoreMovement> Stores,
    IReadOnlyList<StoreMovementActivity> RecentActivity);

/// <summary>A store's actual dispatched quantities and restock request activity.</summary>
public sealed record StoreMovement(
    Guid LocationId,
    string Code,
    string Name,
    decimal IncomingUnits,
    int InboundTransfers,
    decimal OutgoingUnits,
    int OutboundTransfers,
    int RestockRequests,
    int PendingApprovalRequests,
    int AwaitingDispatchRequests,
    DateTimeOffset? LastIncomingAtUtc,
    DateTimeOffset? LastOutgoingAtUtc,
    IReadOnlyList<StoreMovementItem> RecentItems,
    IReadOnlyList<StoreMovementChartPoint> DailyMovement);

/// <summary>A product line from a recent request or completed movement.</summary>
public sealed record StoreMovementItem(
    string ProductName,
    string Reference,
    string Direction,
    decimal Units,
    DateTimeOffset OccurredAtUtc);

/// <summary>One of seven buckets across the selected movement period.</summary>
public sealed record StoreMovementChartPoint(
    int Bucket,
    decimal IncomingUnits,
    decimal OutgoingUnits);

/// <summary>A store request or dispatch recorded during the selected period.</summary>
public sealed record StoreMovementActivity(
    Guid TransferId,
    string Reference,
    string Kind,
    string Status,
    string SourceLocationName,
    string DestinationLocationName,
    decimal ProductUnits,
    DateTimeOffset OccurredAtUtc);

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
    string Status,
    DateOnly? LastSoldOn = null);
