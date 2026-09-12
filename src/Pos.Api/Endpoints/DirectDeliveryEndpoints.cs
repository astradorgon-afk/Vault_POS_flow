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

/// <summary>The body of a direct-delivery authorization creation.</summary>
/// <param name="SupplierId">The supplier allowed to deliver.</param>
/// <param name="StoreLocationId">The store that may receive the delivery.</param>
/// <param name="ValidFrom">The first day the authorization applies.</param>
/// <param name="ValidUntil">The last day the authorization applies.</param>
/// <param name="ProductId">Optional product the authorization is limited to.</param>
/// <param name="ValueCap">Optional cap on the delivered value; null means uncapped.</param>
public sealed record CreateDirectDeliveryAuthorizationBody(
    Guid SupplierId,
    Guid StoreLocationId,
    DateOnly ValidFrom,
    DateOnly ValidUntil,
    Guid? ProductId = null,
    decimal? ValueCap = null);

/// <summary>A direct-to-store delivery authorization as listed.</summary>
public sealed record DirectDeliveryAuthorizationSummary(
    Guid Id,
    string Status,
    Guid SupplierId,
    Guid StoreLocationId,
    DateOnly ValidFrom,
    DateOnly ValidUntil,
    Guid? ProductId,
    decimal? ValueCap,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    Guid? RevokedByUserId,
    DateTimeOffset? RevokedAtUtc);

/// <summary>Direct-to-store delivery authorization endpoints.</summary>
public static class DirectDeliveryEndpoints
{
    /// <summary>Maps the direct-delivery authorization routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapDirectDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/purchasing").WithTags("Purchasing");

        group.MapPost("/direct-delivery-authorizations", CreateDirectDeliveryAuthorizationAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.AuthorizeDirectToStore)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateDirectDeliveryAuthorization")
            .WithSummary("Issues a standing direct-to-store delivery authorization.");

        group.MapGet("/direct-delivery-authorizations", ListDirectDeliveryAuthorizationsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListDirectDeliveryAuthorizations")
            .WithSummary("Lists direct-to-store delivery authorizations.");

        group.MapPost("/direct-delivery-authorizations/{id:guid}/revoke", RevokeDirectDeliveryAuthorizationAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Purchasing.AuthorizeDirectToStore)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RevokeDirectDeliveryAuthorization")
            .WithSummary("Withdraws a direct-to-store delivery authorization.");

        return app;
    }

    private static async Task<IResult> CreateDirectDeliveryAuthorizationAsync(
        [FromBody] CreateDirectDeliveryAuthorizationBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DirectDeliveryAuthorizationId> result = await dispatcher
            .SendAsync(
                new CreateDirectDeliveryAuthorizationCommand(
                    new SupplierId(body.SupplierId),
                    new LocationId(body.StoreLocationId),
                    body.ValidFrom,
                    body.ValidUntil,
                    body.ProductId is { } productId ? new ProductId(productId) : null,
                    body.ValueCap),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListDirectDeliveryAuthorizationsAsync(
        PosDbContext context,
        [FromQuery] Guid? supplierId,
        [FromQuery] Guid? storeLocationId,
        [FromQuery] bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        IQueryable<DirectDeliveryAuthorization> query = context.DirectDeliveryAuthorizations.AsNoTracking();

        if (supplierId is { } supplier)
        {
            query = query.Where(a => a.SupplierId == new SupplierId(supplier));
        }

        if (storeLocationId is { } store)
        {
            query = query.Where(a => a.StoreLocationId == new LocationId(store));
        }

        if (activeOnly)
        {
            query = query.Where(a => a.Status == DirectDeliveryAuthorizationStatus.Active);
        }

        List<DirectDeliveryAuthorizationSummary> authorizations = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => new DirectDeliveryAuthorizationSummary(
                a.Id.Value,
                a.Status.ToString(),
                a.SupplierId.Value,
                a.StoreLocationId.Value,
                a.ValidFrom,
                a.ValidUntil,
                a.ProductId != null ? a.ProductId.Value.Value : null,
                a.ValueCap,
                a.CreatedByUserId.Value,
                a.CreatedAtUtc,
                a.RevokedByUserId != null ? a.RevokedByUserId.Value.Value : null,
                a.RevokedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(authorizations);
    }

    private static async Task<IResult> RevokeDirectDeliveryAuthorizationAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<DirectDeliveryAuthorizationId> result = await dispatcher
            .SendAsync(new RevokeDirectDeliveryAuthorizationCommand(new DirectDeliveryAuthorizationId(id)), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}