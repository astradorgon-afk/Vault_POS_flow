using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>Read-only aggregates used by the owner dashboard.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/dashboard/inventory-overview", GetInventoryOverviewAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.View) { Scope = ScopeSource.None })
            .WithTags("Dashboard")
            .WithName("GetDashboardInventoryOverview")
            .WithSummary("Gets scoped inventory availability and threshold aggregates.");

        return app;
    }

    private static async Task<IResult> GetInventoryOverviewAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        // Scope from the stored authorization, as the other inventory endpoints
        // do: the token carries only the primary location, so a business-wide
        // owner would otherwise see an empty business.
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        LocationId[]? assigned = authorization.HasAllLocations
            ? null
            : [.. authorization.Locations];

        IQueryable<InventoryBalance> balances = context.InventoryBalances.AsNoTracking();
        IQueryable<ProductLocationSetting> settings = context.ProductLocationSettings
            .AsNoTracking()
            .Where(s => s.IsStocked);

        if (assigned is not null)
        {
            balances = balances.Where(b => assigned.Contains(b.LocationId));
            settings = settings.Where(s => assigned.Contains(s.LocationId));
        }

        List<InventoryBalance> balanceRows = await balances.ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ProductLocationSetting> settingRows = await settings.ToListAsync(cancellationToken).ConfigureAwait(false);

        decimal available = balanceRows.Where(b => b.State == InventoryState.Available).Sum(b => b.Quantity);
        decimal inTransit = balanceRows.Where(b => b.State is InventoryState.InTransit or InventoryState.TransitVariance).Sum(b => b.Quantity);
        decimal quarantine = balanceRows.Where(b => b.State == InventoryState.Quarantine).Sum(b => b.Quantity);
        Dictionary<(LocationId Location, ProductId Product), decimal> availableByKey = balanceRows
            .Where(b => b.State == InventoryState.Available)
            .GroupBy(b => (b.LocationId, b.ProductId))
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));

        int lowStock = settingRows.Count(s => availableByKey.GetValueOrDefault((s.LocationId, s.ProductId)) <= s.ReorderPoint);
        int outOfStock = settingRows.Count(s => availableByKey.GetValueOrDefault((s.LocationId, s.ProductId)) <= 0m);
        int overStock = settingRows.Count(s => s.MaximumStock > 0m
            && availableByKey.GetValueOrDefault((s.LocationId, s.ProductId)) > s.MaximumStock);

        return TypedResults.Ok(new DashboardInventoryOverview(
            available, inTransit, quarantine, lowStock, outOfStock, overStock));
    }
}

public sealed record DashboardInventoryOverview(
    decimal AvailableQuantity,
    decimal InTransitQuantity,
    decimal QuarantineQuantity,
    int LowStockItems,
    int OutOfStockItems,
    int OverStockItems);
