using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Purchasing;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Purchasing;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a supplier return creation.</summary>
/// <param name="SupplierId">The supplier receiving the goods back.</param>
/// <param name="LocationId">The location the stock sits in.</param>
/// <param name="Lines">The lines to return.</param>
public sealed record CreateSupplierReturnBody(
    Guid SupplierId,
    Guid LocationId,
    IReadOnlyList<CreateSupplierReturnLineBody> Lines);

/// <summary>One line of a supplier return creation.</summary>
/// <param name="ProductId">The product being returned.</param>
/// <param name="BatchId">The specific lot, for batch-tracked products.</param>
/// <param name="SourceState">The state the stock is returned from: Damaged, Expired or Quarantine.</param>
/// <param name="Quantity">The quantity to return, in the base unit.</param>
/// <param name="UnitCost">The valuation cost per unit.</param>
/// <param name="Reason">Why the goods are being returned.</param>
/// <param name="Notes">Required when the reason is Other.</param>
public sealed record CreateSupplierReturnLineBody(
    Guid ProductId,
    Guid? BatchId,
    InventoryState SourceState,
    decimal Quantity,
    decimal UnitCost,
    SupplierReturnReason Reason,
    string? Notes = null);

/// <summary>The body of a supplier return dispatch.</summary>
/// <param name="SupplierAuthorizationNumber">The supplier's return-authorization number.</param>
public sealed record DispatchSupplierReturnBody(string SupplierAuthorizationNumber);

/// <summary>A supplier return line.</summary>
public sealed record SupplierReturnLineSummary(
    int LineNo,
    Guid ProductId,
    Guid? BatchId,
    string SourceState,
    decimal Quantity,
    decimal UnitCost,
    string Reason,
    string? Notes);

/// <summary>A supplier return with its lines.</summary>
public sealed record SupplierReturnDetail(
    Guid Id,
    string Number,
    string Status,
    Guid SupplierId,
    Guid LocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    Guid? ApprovedByUserId,
    DateTimeOffset? ApprovedAtUtc,
    string? SupplierAuthorizationNumber,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    decimal TotalValue,
    IReadOnlyList<SupplierReturnLineSummary> Lines);

/// <summary>Supplier return endpoints.</summary>
public static class SupplierReturnEndpoints
{
    /// <summary>Maps the supplier return routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSupplierReturnEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/purchasing").WithTags("Purchasing");

        group.MapPost("/supplier-returns", CreateSupplierReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Return)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateSupplierReturn")
            .WithSummary("Raises a draft supplier return.");

        group.MapPost("/supplier-returns/{id:guid}/submit", SubmitSupplierReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Return)
            {
                Scope = ScopeSource.None,
            })
            .WithName("SubmitSupplierReturn")
            .WithSummary("Submits a supplier return for approval.");

        group.MapPost("/supplier-returns/{id:guid}/approve", ApproveSupplierReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ApproveSupplierReturn")
            .WithSummary("Approves a submitted supplier return.");

        group.MapPost("/supplier-returns/{id:guid}/reject", RejectSupplierReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RejectSupplierReturn")
            .WithSummary("Rejects a submitted supplier return, sending it back to draft.");

        group.MapPost("/supplier-returns/{id:guid}/dispatch", DispatchSupplierReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Return)
            {
                Scope = ScopeSource.None,
            })
            .WithName("DispatchSupplierReturn")
            .WithSummary("Dispatches an approved return to the supplier and posts the ledger.");

        group.MapPost("/supplier-returns/{id:guid}/confirm", ConfirmSupplierReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Return)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ConfirmSupplierReturn")
            .WithSummary("Records that the supplier confirmed the return.");

        group.MapGet("/supplier-returns/{id:guid}", GetSupplierReturnAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetSupplierReturn")
            .WithSummary("Gets one supplier return by identifier.");

        return app;
    }

    private static async Task<IResult> CreateSupplierReturnAsync(
        [FromBody] CreateSupplierReturnBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<SupplierReturnId> result = await dispatcher
            .SendAsync(
                new CreateSupplierReturnCommand(
                    new SupplierId(body.SupplierId),
                    new LocationId(body.LocationId),
                    [.. body.Lines.Select(l => new SupplierReturnLineSpec(
                        new ProductId(l.ProductId),
                        l.BatchId is { } batchId ? new BatchId(batchId) : null,
                        l.SourceState,
                        l.Quantity,
                        l.UnitCost,
                        l.Reason,
                        l.Notes))]),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> SubmitSupplierReturnAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchReturnAsync(
            new SubmitSupplierReturnCommand(new SupplierReturnId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ApproveSupplierReturnAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchReturnAsync(
            new ApproveSupplierReturnCommand(new SupplierReturnId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> RejectSupplierReturnAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchReturnAsync(
            new RejectSupplierReturnCommand(new SupplierReturnId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DispatchSupplierReturnAsync(
        Guid id,
        [FromBody] DispatchSupplierReturnBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchReturnAsync(
            new DispatchSupplierReturnCommand(new SupplierReturnId(id), body.SupplierAuthorizationNumber),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ConfirmSupplierReturnAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchReturnAsync(
            new ConfirmSupplierReturnCommand(new SupplierReturnId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DispatchReturnAsync(
        ICommand<SupplierReturnId> command,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<SupplierReturnId> result = await dispatcher
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> GetSupplierReturnAsync(
        PosDbContext context,
        Guid id,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SupplierReturnDetail? detail = await context.SupplierReturns
            .AsNoTracking()
            .Where(r => r.Id == new SupplierReturnId(id))
            .Select(r => new SupplierReturnDetail(
                r.Id.Value,
                r.Number,
                r.Status.ToString(),
                r.SupplierId.Value,
                r.LocationId.Value,
                r.CreatedByUserId.Value,
                r.CreatedAtUtc,
                r.ApprovedByUserId != null ? r.ApprovedByUserId.Value.Value : null,
                r.ApprovedAtUtc,
                r.SupplierAuthorizationNumber,
                r.DispatchedAtUtc,
                r.ConfirmedAtUtc,
                r.TotalValue,
                r.Lines
                    .OrderBy(l => l.LineNo)
                    .Select(l => new SupplierReturnLineSummary(
                        l.LineNo,
                        l.ProductId.Value,
                        l.BatchId != null ? l.BatchId.Value.Value : null,
                        l.SourceState.ToString(),
                        l.Quantity,
                        l.UnitCost,
                        l.Reason.ToString(),
                        l.Notes))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return detail is null
            ? ProblemDetailsMapping.ToProblem(
                Result.Failure(PurchasingErrors.ReturnUnknown(new SupplierReturnId(id))),
                currentUser.CorrelationId.Value)
            : TypedResults.Ok(detail);
    }
}