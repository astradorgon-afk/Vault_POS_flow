using Microsoft.AspNetCore.Mvc;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Catalog;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Api.Endpoints;

/// <summary>The note recorded with a decision.</summary>
/// <param name="Note">Why; required when rejecting.</param>
public sealed record ProductChangeDecisionBody(string? Note);

/// <summary>
/// The owner's queue of catalogue changes other people have asked for.
/// </summary>
/// <remarks>
/// A holder of <c>product.edit</c> who is not also an approver cannot write to
/// a product directly: their command is held here with the reason they gave,
/// and is applied only when an approver releases it.
/// </remarks>
public static class ProductChangeApprovalEndpoints
{
    /// <summary>The header carrying the reason a catalogue change is needed.</summary>
    public const string ReasonHeader = "X-Change-Reason";

    /// <summary>Maps the change-approval routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapProductChangeApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/catalog/change-requests").WithTags("Catalog");

        group.MapGet(string.Empty, ListAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.View) { Scope = ScopeSource.None })
            .WithName("ListProductChangeRequests")
            .WithSummary("Lists catalogue changes awaiting or past a decision.");

        group.MapPost("/{requestId:guid}/approve", ApproveAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.ApproveChanges) { Scope = ScopeSource.None })
            .WithName("ApproveProductChange")
            .WithSummary("Approves a held change and applies it.");

        group.MapPost("/{requestId:guid}/reject", RejectAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.ApproveChanges) { Scope = ScopeSource.None })
            .WithName("RejectProductChange")
            .WithSummary("Refuses a held change, with a reason.");

        group.MapPost("/{requestId:guid}/withdraw", WithdrawAsync)
            .RequireAuthorization()
            .WithMetadata(new RequirePermissionAttribute(Permissions.Catalog.Edit) { Scope = ScopeSource.None })
            .WithName("WithdrawProductChange")
            .WithSummary("Takes back a change the caller raised.");

        return app;
    }

    /// <summary>
    /// Captures the reason a catalogue change was submitted with, so the gate
    /// can store it against the request it raises.
    /// </summary>
    /// <remarks>
    /// Applied to the curation group as a whole rather than read in each
    /// handler, so a route added later carries it without being changed.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>The endpoint's result.</returns>
    public static ValueTask<object?> CaptureReasonAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.HttpContext.Request.Headers.TryGetValue(ReasonHeader, out Microsoft.Extensions.Primitives.StringValues given))
        {
            context.HttpContext.RequestServices.GetRequiredService<ProductChangeReason>().Reason = given.ToString();
        }

        return next(context);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] bool? pendingOnly,
        [FromQuery] Guid? productId,
        [FromQuery] int? limit,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ProductChangeRequestView>> result = await dispatcher
            .QueryAsync(
                new ListProductChangeRequestsQuery(
                    pendingOnly ?? true,
                    productId is { } id ? new ProductId(id) : null,
                    limit ?? 50),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static Task<IResult> ApproveAsync(
        Guid requestId,
        [FromBody] ProductChangeDecisionBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => DecideAsync(
            dispatcher,
            currentUser,
            new ApproveProductChangeCommand(new ProductChangeRequestId(requestId), body?.Note),
            cancellationToken);

    private static Task<IResult> RejectAsync(
        Guid requestId,
        [FromBody] ProductChangeDecisionBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => DecideAsync(
            dispatcher,
            currentUser,
            new RejectProductChangeCommand(new ProductChangeRequestId(requestId), body?.Note),
            cancellationToken);

    private static Task<IResult> WithdrawAsync(
        Guid requestId,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => DecideAsync(
            dispatcher,
            currentUser,
            new WithdrawProductChangeCommand(new ProductChangeRequestId(requestId)),
            cancellationToken);

    private static async Task<IResult> DecideAsync(
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        ICommand<ProductChangeRequestId> command,
        CancellationToken cancellationToken)
    {
        Result<ProductChangeRequestId> result = await dispatcher
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }
}
