using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>How much a location moved under one movement type.</summary>
/// <param name="MovementType">The movement type.</param>
/// <param name="Movements">How many ledger legs it posted.</param>
/// <param name="UnitsIn">Units that came in under it.</param>
/// <param name="UnitsOut">Units that went out under it.</param>
public sealed record LocationHistoryMovement(
    string MovementType,
    int Movements,
    decimal UnitsIn,
    decimal UnitsOut);

/// <summary>One product still on a location's books.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Sku">Its SKU.</param>
/// <param name="Name">Its name.</param>
/// <param name="Quantity">What remains, across every state.</param>
/// <param name="Value">What that is worth at average cost.</param>
public sealed record LocationHistoryStock(
    Guid ProductId,
    string Sku,
    string Name,
    decimal Quantity,
    decimal Value);

/// <summary>Someone who worked at a location, and how much.</summary>
/// <param name="UserId">The user.</param>
/// <param name="Name">Their display name.</param>
/// <param name="Actions">How many recorded actions they took here.</param>
/// <param name="LastActionAtUtc">When they last acted here.</param>
public sealed record LocationHistoryPerson(
    Guid UserId,
    string Name,
    int Actions,
    DateTimeOffset LastActionAtUtc);

/// <summary>Everything one location did, over its whole life.</summary>
public sealed record LocationHistory(
    Guid LocationId,
    string Code,
    string Name,
    string Kind,
    bool IsActive,
    DateOnly OpenedOn,
    DateOnly? ClosedOn,
    DateTimeOffset? FirstActivityAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    decimal UnitsIn,
    decimal UnitsOut,
    int MovementCount,
    int SaleCount,
    decimal SalesNetTotal,
    int TransfersIn,
    int TransfersOut,
    int GoodsReceipts,
    int StockCounts,
    int StockAdjustments,
    decimal StockRemaining,
    decimal StockRemainingValue,
    IReadOnlyList<LocationHistoryMovement> Movements,
    IReadOnlyList<LocationHistoryStock> Stock,
    IReadOnlyList<LocationHistoryPerson> People);

/// <summary>
/// One location's whole history, gathered in a single read.
/// </summary>
/// <remarks>
/// A closed store's records do not disappear, but they do scatter: its stock
/// movements sit in the ledger, its sales in the sales tables, its transfers at
/// both ends of a route, its people in the audit log. Asking an owner to visit
/// five screens and filter each one to a store that no longer appears in their
/// pickers is how history gets lost in practice. This gathers the lot.
/// </remarks>
public static class LocationHistoryEndpoints
{
    /// <summary>Maps the location history endpoint.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapLocationHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/locations/{id:guid}/history", GetAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Administration.ManageLocations)
            {
                Scope = ScopeSource.None,
            })
            .WithTags("Locations")
            .WithName("GetLocationHistory")
            .WithSummary("Gathers everything one location did, for review after it closes.");

        return app;
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        PosDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        LocationId locationId = new(id);

        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        if (location is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(LocationErrors.Unknown(locationId)),
                currentUser.CorrelationId.Value);
        }

        // The ledger is the one place every kind of stock movement meets, so
        // "products in and out" is read from it rather than from each document
        // type in turn.
        var movements = await context.InventoryMovements
            .AsNoTracking()
            .Where(m => m.LocationId == locationId)
            .GroupBy(m => m.MovementType)
            .Select(g => new
            {
                MovementType = g.Key,
                Movements = g.Count(),
                UnitsIn = g.Where(m => m.QuantityDelta > 0m).Sum(m => (decimal?)m.QuantityDelta) ?? 0m,
                UnitsOut = g.Where(m => m.QuantityDelta < 0m).Sum(m => (decimal?)m.QuantityDelta) ?? 0m,
                First = g.Min(m => (DateTimeOffset?)m.OccurredAtUtc),
                Last = g.Max(m => (DateTimeOffset?)m.OccurredAtUtc),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sales = await context.Sales
            .AsNoTracking()
            .Where(s => s.LocationId == locationId && s.Status == SaleStatus.Completed)
            .GroupBy(s => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Net = g.Sum(s => (decimal?)s.NetTotal) ?? 0m,
                Last = g.Max(s => (DateTimeOffset?)s.CompletedAtUtc),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        int transfersOut = await context.Transfers.AsNoTracking()
            .CountAsync(t => t.SourceLocationId == locationId && t.DispatchedAtUtc != null, cancellationToken)
            .ConfigureAwait(false);
        int transfersIn = await context.Transfers.AsNoTracking()
            .CountAsync(t => t.DestinationLocationId == locationId && t.ReceivedAtUtc != null, cancellationToken)
            .ConfigureAwait(false);
        int receipts = await context.GoodsReceipts.AsNoTracking()
            .CountAsync(r => r.DestinationLocationId == locationId, cancellationToken)
            .ConfigureAwait(false);
        int counts = await context.InventoryCounts.AsNoTracking()
            .CountAsync(c => c.LocationId == locationId, cancellationToken)
            .ConfigureAwait(false);
        int adjustments = await context.StockAdjustments.AsNoTracking()
            .CountAsync(a => a.LocationId == locationId, cancellationToken)
            .ConfigureAwait(false);

        // What the store was still holding when the music stopped. A closed
        // location is not required to be empty, so this is frequently the most
        // important number on the page.
        var balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.LocationId == locationId && b.Quantity != 0m)
            .GroupBy(b => b.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                Quantity = g.Sum(b => b.Quantity),
                Value = g.Sum(b => b.TotalValue),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        ProductId[] productIds = [.. balances.Select(b => b.ProductId)];
        Dictionary<Guid, (string Sku, string Name)> products = productIds.Length == 0
            ? []
            : await context.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, Sku = p.Sku.Value, p.Name })
                .ToDictionaryAsync(p => p.Id.Value, p => (p.Sku, p.Name), cancellationToken)
                .ConfigureAwait(false);

        var actors = await context.AuditLog
            .AsNoTracking()
            .Where(entry => entry.LocationId == locationId && entry.UserId != null)
            .GroupBy(entry => entry.UserId!.Value)
            .Select(g => new
            {
                UserId = g.Key,
                Actions = g.Count(),
                Last = g.Max(entry => entry.OccurredAtUtc),
            })
            .OrderByDescending(a => a.Actions)
            .Take(50)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Guid[] actorIds = [.. actors.Select(a => a.UserId.Value)];
        Dictionary<Guid, string> actorNames = actorIds.Length == 0
            ? []
            : await context.Users.AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .Select(user => new { user.Id, user.DisplayName })
                .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken)
                .ConfigureAwait(false);

        DateTimeOffset? first = movements.Count == 0 ? null : movements.Min(m => m.First);
        DateTimeOffset? last = movements.Count == 0 ? null : movements.Max(m => m.Last);

        if (sales?.Last is { } lastSale && (last is null || lastSale > last))
        {
            last = lastSale;
        }

        return TypedResults.Ok(new LocationHistory(
            location.Id.Value,
            location.Code,
            location.Name,
            location.Kind.ToString(),
            location.IsActive,
            location.OpenedOn,
            location.ClosedOn,
            first,
            last,
            movements.Sum(m => m.UnitsIn),
            Math.Abs(movements.Sum(m => m.UnitsOut)),
            movements.Sum(m => m.Movements),
            sales?.Count ?? 0,
            sales?.Net ?? 0m,
            transfersIn,
            transfersOut,
            receipts,
            counts,
            adjustments,
            balances.Sum(b => b.Quantity),
            balances.Sum(b => b.Value),
            [.. movements
                .OrderByDescending(m => m.UnitsIn + Math.Abs(m.UnitsOut))
                .Select(m => new LocationHistoryMovement(
                    m.MovementType.ToString(),
                    m.Movements,
                    m.UnitsIn,
                    Math.Abs(m.UnitsOut)))],
            [.. balances
                .OrderByDescending(b => b.Value)
                .Select(b => new LocationHistoryStock(
                    b.ProductId.Value,
                    products.TryGetValue(b.ProductId.Value, out (string Sku, string Name) found) ? found.Sku : "—",
                    products.TryGetValue(b.ProductId.Value, out (string Sku, string Name) named) ? named.Name : "Unknown product",
                    b.Quantity,
                    b.Value))],
            [.. actors.Select(a => new LocationHistoryPerson(
                a.UserId.Value,
                actorNames.GetValueOrDefault(a.UserId.Value, "Unknown user"),
                a.Actions,
                a.Last))]));
    }
}
