using Microsoft.Extensions.Options;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Identity;
using Pos.Application.Inventory;
using Pos.Domain.Common;
using Pos.Infrastructure.Configuration;

namespace Pos.Api.Endpoints;

/// <summary>Inventory reconciliation endpoints.</summary>
public static class InventoryEndpoints
{
    private static readonly Error MaintenanceDisabled = Error.Unavailable(
        "maintenance.disabled",
        "Set Maintenance:AllowBalanceRebuild in configuration to true to allow this operation.");

    /// <summary>Maps the inventory reconciliation routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/inventory").WithTags("Inventory");

        group.MapPost("/reconcile", ReconcileAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.RebuildBalances)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReconcileInventory")
            .WithSummary("Replays the ledger to detect drift in the balance projection.");

        group.MapPost("/rebuild-balances", RebuildBalancesAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Inventory.RebuildBalances)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RebuildInventoryBalances")
            .WithSummary("Drops and rebuilds the balance projection from the ledger.");

        return app;
    }

    private static async Task<IResult> ReconcileAsync(
        IBalanceReconciler reconciler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<ReconciliationReport> result = await reconciler
            .DetectAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> RebuildBalancesAsync(
        IBalanceReconciler reconciler,
        ICurrentUser currentUser,
        IOptions<MaintenanceOptions> maintenance,
        CancellationToken cancellationToken)
    {
        if (!maintenance.Value.AllowBalanceRebuild)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(MaintenanceDisabled),
                currentUser.CorrelationId.Value);
        }

        Result<ReconciliationReport> result = await reconciler
            .RebuildAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}