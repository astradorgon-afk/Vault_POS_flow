using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Purchasing;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>One product on a new supply request.</summary>
/// <param name="ProductId">The product.</param>
/// <param name="Quantity">How much is wanted, in the product's base unit.</param>
/// <param name="Note">Anything the owner should know when buying it.</param>
public sealed record CreateSupplyRequestLineBody(Guid ProductId, decimal Quantity, string? Note = null);

/// <summary>The body of a new supply request.</summary>
/// <param name="WarehouseLocationId">The warehouse that wants the goods.</param>
/// <param name="Lines">What it wants.</param>
/// <param name="Note">An optional note for the owner.</param>
public sealed record CreateSupplyRequestBody(
    Guid WarehouseLocationId,
    IReadOnlyList<CreateSupplyRequestLineBody> Lines,
    string? Note = null);

/// <summary>One request line going onto a purchase order.</summary>
/// <param name="LineNo">The request line.</param>
/// <param name="UnitOfMeasureId">The product's base unit.</param>
/// <param name="Quantity">How much to order.</param>
/// <param name="UnitCost">The agreed cost per base unit.</param>
public sealed record SupplyRequestOrderLineBody(
    int LineNo,
    Guid UnitOfMeasureId,
    decimal Quantity,
    decimal UnitCost);

/// <summary>The body of a purchase order raised against a supply request.</summary>
/// <param name="SupplierId">The managed supplier, or null when naming a new one.</param>
/// <param name="Lines">The request lines going onto this order.</param>
/// <param name="CurrencyCode">Three-letter ISO-4217 code; defaults to PHP.</param>
/// <param name="ExpectedAtUtc">Expected delivery instant, or null.</param>
/// <param name="CustomSupplierName">Name of a new supplier, used when no supplier is named.</param>
public sealed record OrderSupplyRequestBody(
    Guid? SupplierId,
    IReadOnlyList<SupplyRequestOrderLineBody> Lines,
    string? CurrencyCode = null,
    DateTimeOffset? ExpectedAtUtc = null,
    string? CustomSupplierName = null);

/// <summary>The body of a rejection or withdrawal.</summary>
/// <param name="Reason">Why.</param>
public sealed record SupplyRequestDecisionBody(string? Reason = null);

/// <summary>One line of a supply request, as read.</summary>
public sealed record SupplyRequestLineSummary(
    int LineNo,
    Guid ProductId,
    decimal Quantity,
    decimal OrderedQuantity,
    decimal OutstandingQuantity,
    string? Note);

/// <summary>A purchase order raised against a supply request.</summary>
public sealed record SupplyRequestOrderSummary(
    Guid PurchaseOrderId,
    string? Number,
    string Status,
    DateTimeOffset OrderedAtUtc);

/// <summary>A supply request as listed or read.</summary>
public sealed record SupplyRequestSummary(
    Guid Id,
    string Number,
    Guid DestinationLocationId,
    string Status,
    string? Note,
    Guid RequestedByUserId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionReason,
    IReadOnlyList<SupplyRequestLineSummary> Lines,
    IReadOnlyList<SupplyRequestOrderSummary> Orders);

/// <summary>
/// The warehouse's own restock requests, and the purchase orders raised to fill
/// them.
/// </summary>
/// <remarks>
/// A store asks the warehouse for stock and gets a transfer. The warehouse has
/// nobody to ask, so its request goes to the owner, who buys what it needs.
/// </remarks>
public static class SupplyRequestEndpoints
{
    /// <summary>Maps the supply request endpoints.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSupplyRequestEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/supply-requests").WithTags("Purchasing");

        group.MapGet(string.Empty, ListAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Request) { Scope = ScopeSource.None })
            .WithName("ListSupplyRequests")
            .WithSummary("Lists the warehouse's supply requests.");

        group.MapGet("/{id:guid}", GetAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Request) { Scope = ScopeSource.None })
            .WithName("GetSupplyRequest")
            .WithSummary("Gets one supply request with its lines and orders.");

        group.MapPost(string.Empty, CreateAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Request) { Scope = ScopeSource.None })
            .WithName("CreateSupplyRequest")
            .WithSummary("Raises a request for the main warehouse to be restocked.");

        group.MapPost("/{id:guid}/orders", OrderAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Create) { Scope = ScopeSource.None })
            .WithName("OrderSupplyRequest")
            .WithSummary("Raises a draft purchase order against a supply request.");

        group.MapPost("/{id:guid}/reject", RejectAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.Create) { Scope = ScopeSource.None })
            .WithName("RejectSupplyRequest")
            .WithSummary("Turns a supply request down.");

        group.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Request) { Scope = ScopeSource.None })
            .WithName("CancelSupplyRequest")
            .WithSummary("Withdraws a supply request.");

        return app;
    }

    private static async Task<IResult> ListAsync(
        PosDbContext context,
        [FromQuery] bool? openOnly,
        CancellationToken cancellationToken)
    {
        IQueryable<SupplyRequest> query = context.SupplyRequests
            .AsNoTracking()
            .Include(r => r.Lines)
            .Include(r => r.Orders);

        if (openOnly == true)
        {
            query = query.Where(r =>
                r.Status == SupplyRequestStatus.Submitted || r.Status == SupplyRequestStatus.PartiallyOrdered);
        }

        List<SupplyRequest> requests = await query
            .OrderByDescending(r => r.RequestedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(await ProjectAsync(context, requests, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        PosDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        SupplyRequest? request = await context.SupplyRequests
            .AsNoTracking()
            .Include(r => r.Lines)
            .Include(r => r.Orders)
            .FirstOrDefaultAsync(r => r.Id == new SupplyRequestId(id), cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(PurchasingErrors.SupplyRequestUnknown(new SupplyRequestId(id))),
                currentUser.CorrelationId.Value);
        }

        IReadOnlyList<SupplyRequestSummary> projected =
            await ProjectAsync(context, [request], cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(projected[0]);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateSupplyRequestBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        Result<SupplyRequestId> result = await dispatcher
            .SendAsync(
                new CreateSupplyRequestCommand(
                    new LocationId(body.WarehouseLocationId),
                    [.. (body.Lines ?? []).Select(l => new SupplyRequestLineSpec(
                        new ProductId(l.ProductId), l.Quantity, l.Note))],
                    body.Note),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> OrderAsync(
        Guid id,
        [FromBody] OrderSupplyRequestBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        Result<PurchaseOrderId> result = await dispatcher
            .SendAsync(
                new OrderSupplyRequestCommand(
                    new SupplyRequestId(id),
                    body.SupplierId is { } supplierId ? new SupplierId(supplierId) : SupplierId.Empty,
                    [.. (body.Lines ?? []).Select(l => new SupplyRequestOrderLineSpec(
                        l.LineNo, new UnitOfMeasureId(l.UnitOfMeasureId), l.Quantity, l.UnitCost))],
                    body.CurrencyCode,
                    body.ExpectedAtUtc,
                    body.CustomSupplierName),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> RejectAsync(
        Guid id,
        [FromBody] SupplyRequestDecisionBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<SupplyRequestId> result = await dispatcher
            .SendAsync(new RejectSupplyRequestCommand(new SupplyRequestId(id), body?.Reason), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        [FromBody] SupplyRequestDecisionBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<SupplyRequestId> result = await dispatcher
            .SendAsync(new CancelSupplyRequestCommand(new SupplyRequestId(id), body?.Reason), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    /// <summary>
    /// Shapes requests for the wire, resolving each linked order's number and
    /// status so the owner can see what a request turned into without opening
    /// every order.
    /// </summary>
    private static async Task<IReadOnlyList<SupplyRequestSummary>> ProjectAsync(
        PosDbContext context,
        IReadOnlyList<SupplyRequest> requests,
        CancellationToken cancellationToken)
    {
        PurchaseOrderId[] orderIds = [.. requests
            .SelectMany(r => r.Orders)
            .Select(o => o.PurchaseOrderId)
            .Distinct()];

        Dictionary<Guid, (string? Number, string Status)> orders = orderIds.Length == 0
            ? []
            : await context.PurchaseOrders
                .AsNoTracking()
                .Where(o => orderIds.Contains(o.Id))
                .Select(o => new { o.Id, o.Number, o.Status })
                .ToDictionaryAsync(
                    o => o.Id.Value,
                    o => ((string?)o.Number, o.Status.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);

        return [.. requests.Select(request => new SupplyRequestSummary(
            request.Id.Value,
            request.Number,
            request.DestinationLocationId.Value,
            request.Status.ToString(),
            request.Note,
            request.RequestedByUserId.Value,
            request.RequestedAtUtc,
            request.DecidedAtUtc,
            request.DecisionReason,
            [.. request.Lines.OrderBy(l => l.LineNo).Select(l => new SupplyRequestLineSummary(
                l.LineNo,
                l.ProductId.Value,
                l.Quantity,
                l.OrderedQuantity,
                l.OutstandingQuantity,
                l.Note))],
            [.. request.Orders.Select(o => new SupplyRequestOrderSummary(
                o.PurchaseOrderId.Value,
                orders.TryGetValue(o.PurchaseOrderId.Value, out (string? Number, string Status) found) ? found.Number : null,
                orders.TryGetValue(o.PurchaseOrderId.Value, out (string? Number, string Status) state) ? state.Status : "Unknown",
                o.OrderedAtUtc))]))];
    }
}
