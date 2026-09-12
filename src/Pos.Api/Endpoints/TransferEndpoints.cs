using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Transfers;
using Pos.Domain.Common;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a transfer creation.</summary>
/// <param name="SourceLocationId">The location the stock leaves.</param>
/// <param name="DestinationLocationId">The location the stock is destined for.</param>
/// <param name="Lines">The lines to move, in the products' base units.</param>
public sealed record CreateTransferBody(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<CreateTransferLineBody> Lines);

/// <summary>One line of a transfer creation.</summary>
/// <param name="ProductId">The product to move.</param>
/// <param name="Quantity">The requested quantity, in the base unit.</param>
/// <param name="Note">An optional picking note.</param>
public sealed record CreateTransferLineBody(Guid ProductId, decimal Quantity, string? Note = null);

/// <summary>An approval-time change to a requested quantity.</summary>
/// <param name="LineNo">The line to change.</param>
/// <param name="RequestedQuantity">The new requested quantity.</param>
public sealed record TransferAmendmentBody(int LineNo, decimal RequestedQuantity);

/// <summary>The body of a transfer approval.</summary>
/// <param name="Amendments">Optional quantity amendments applied on approval.</param>
/// <param name="Note">An optional approval note.</param>
public sealed record ApproveTransferBody(
    IReadOnlyList<TransferAmendmentBody>? Amendments = null,
    string? Note = null);

/// <summary>One picked lot of a transfer.</summary>
/// <param name="LineNo">The transfer line being satisfied.</param>
/// <param name="BatchId">The lot, for batch-tracked products.</param>
/// <param name="Quantity">The picked quantity.</param>
public sealed record TransferPickBody(Guid? BatchId, int LineNo, decimal Quantity);

/// <summary>The body of a transfer pick.</summary>
/// <param name="Allocations">The picked lots and quantities.</param>
public sealed record PickTransferBody(IReadOnlyList<TransferPickBody> Allocations);

/// <summary>The body of a dispatch cancellation.</summary>
/// <param name="Reason">Why the dispatch was undone; recorded on the ledger.</param>
public sealed record CancelTransferDispatchBody(string Reason);

/// <summary>One arrival receipt line, keyed to the allocation that was dispatched.</summary>
/// <param name="LineNo">The transfer line being receipted.</param>
/// <param name="BatchId">The lot as picked; required when the product is batch-tracked.</param>
/// <param name="ReceivedQuantity">The quantity that arrived in good condition.</param>
/// <param name="DamagedQuantity">The quantity that arrived damaged.</param>
public sealed record TransferReceiveBody(
    int LineNo,
    Guid? BatchId,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);

/// <summary>The body of a transfer arrival receipt.</summary>
/// <param name="Receives">What arrived, per picked allocation.</param>
public sealed record ReceiveTransferBody(IReadOnlyList<TransferReceiveBody> Receives);

/// <summary>The body of a discrepancy resolution.</summary>
/// <param name="Outcome">Whether the stock was found or written off.</param>
/// <param name="Note">An optional resolution note.</param>
public sealed record ResolveTransferDiscrepancyBody(
    TransferDiscrepancyResolutionOutcome Outcome,
    string? Note = null);

/// <summary>A transfer as it appears in a list.</summary>
public sealed record TransferSummary(
    Guid Id,
    string Number,
    string Status,
    Guid SourceLocationId,
    Guid DestinationLocationId,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? ReceivedAtUtc,
    decimal TotalValue,
    int LineCount);

/// <summary>One step in a transfer's custody timeline.</summary>
public sealed record TransferCustodyEventSummary(
    int Sequence,
    string Kind,
    Guid ActorUserId,
    DateTimeOffset OccurredAtUtc,
    string? Note);

/// <summary>Transfer order endpoints.</summary>
public static class TransferEndpoints
{
    /// <summary>Maps the transfer routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/api/v1/transfers").WithTags("Transfers");

        group.MapGet("/", ListTransfersAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListTransfers")
            .WithSummary("Lists transfers touching the caller's scoped locations.");

        group.MapPost("/", CreateTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Request)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CreateTransfer")
            .WithSummary("Raises a draft transfer request.");

        group.MapPost("/{id:guid}/submit", SubmitTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Request)
            {
                Scope = ScopeSource.None,
            })
            .WithName("SubmitTransfer")
            .WithSummary("Submits a draft transfer for review.");

        group.MapPost("/{id:guid}/review", ReviewTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReviewTransfer")
            .WithSummary("Marks a submitted transfer as being reviewed.");

        group.MapPost("/{id:guid}/approve", ApproveTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ApproveTransfer")
            .WithSummary("Approves a reviewed transfer, optionally amending quantities.");

        group.MapPost("/{id:guid}/reject", RejectTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RejectTransfer")
            .WithSummary("Rejects a transfer and sends it back to draft.");

        group.MapPost("/{id:guid}/pick", PickTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Pick)
            {
                Scope = ScopeSource.None,
            })
            .WithName("PickTransfer")
            .WithSummary("Records what was picked at the source.");

        group.MapPost("/{id:guid}/ready", ReadyTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Pick)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReadyTransfer")
            .WithSummary("Marks picking complete; the transfer may now be dispatched.");

        group.MapPost("/{id:guid}/dispatch", DispatchTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Dispatch)
            {
                Scope = ScopeSource.None,
            })
            .WithName("DispatchTransfer")
            .WithSummary("Dispatches a ready transfer and posts the ledger.");

        group.MapPost("/{id:guid}/cancel-dispatch", CancelTransferDispatchAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Dispatch)
            {
                Scope = ScopeSource.None,
            })
            .WithName("CancelTransferDispatch")
            .WithSummary("Cancels a dispatched transfer and reverses the ledger.");

        group.MapPost("/{id:guid}/receive", ReceiveTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Receive)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReceiveTransfer")
            .WithSummary("Records what arrived at the destination and posts the ledger.");

        group.MapPost("/{id:guid}/verify", VerifyTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Verify)
            {
                Scope = ScopeSource.None,
            })
            .WithName("VerifyTransfer")
            .WithSummary("Verifies and closes a fully accounted transfer.");

        group.MapPost("/{id:guid}/discrepancies/{discrepancyId:guid}/resolve", ResolveTransferDiscrepancyAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Reconcile)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ResolveTransferDiscrepancy")
            .WithSummary("Resolves an arrival discrepancy, posting the ledger.");

        group.MapGet("/{id:guid}/custody", GetTransferCustodyAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetTransferCustody")
            .WithSummary("Gets a transfer's custody timeline.");

        return app;
    }

    private static async Task<IResult> ListTransfersAsync(
        PosDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        IQueryable<Transfer> query = context.Transfers.AsNoTracking();

        // The list is scoped to the caller's locations unless they act
        // business-wide: a store manager only sees transfers into and out of
        // their own store.
        if (!currentUser.HasAllLocations)
        {
            LocationId[] scoped = [.. currentUser.AssignedLocations];
            query = query.Where(t => scoped.Contains(t.SourceLocationId) || scoped.Contains(t.DestinationLocationId));
        }

        List<TransferSummary> summaries = await query
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new TransferSummary(
                t.Id.Value,
                t.Number,
                t.Status.ToString(),
                t.SourceLocationId.Value,
                t.DestinationLocationId.Value,
                t.CreatedByUserId.Value,
                t.CreatedAtUtc,
                t.DispatchedAtUtc,
                t.ReceivedAtUtc,
                t.TotalValue,
                t.Lines.Count))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(summaries);
    }

    private static async Task<IResult> CreateTransferAsync(
        [FromBody] CreateTransferBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<TransferOrderId> result = await dispatcher
            .SendAsync(
                new CreateTransferCommand(
                    new LocationId(body.SourceLocationId),
                    new LocationId(body.DestinationLocationId),
                    [.. body.Lines.Select(l => new TransferLineSpec(
                        new ProductId(l.ProductId),
                        l.Quantity,
                        l.Note))]),
                cancellationToken)
            .ConfigureAwait(false);

        return Complete(result, currentUser);
    }

    private static async Task<IResult> SubmitTransferAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new SubmitTransferCommand(new TransferOrderId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ReviewTransferAsync(
        Guid id,
        [FromBody] TransferReviewBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ReviewTransferCommand(new TransferOrderId(id), body?.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ApproveTransferAsync(
        Guid id,
        [FromBody] ApproveTransferBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ApproveTransferCommand(
                new TransferOrderId(id),
                body?.Amendments is { Count: > 0 } amendments
                    ? [.. amendments.Select(a => new TransferLineAmendment(a.LineNo, a.RequestedQuantity))]
                    : null,
                body?.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> RejectTransferAsync(
        Guid id,
        [FromBody] TransferReviewBody? body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new RejectTransferCommand(new TransferOrderId(id), body?.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> PickTransferAsync(
        Guid id,
        [FromBody] PickTransferBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new PickTransferCommand(
                new TransferOrderId(id),
                [.. body.Allocations.Select(a => new TransferPickRequestItem(
                    a.LineNo,
                    a.BatchId is { } batchId ? new BatchId(batchId) : null,
                    a.Quantity))]),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ReadyTransferAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ReadyTransferCommand(new TransferOrderId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DispatchTransferAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new DispatchTransferCommand(new TransferOrderId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> CancelTransferDispatchAsync(
        Guid id,
        [FromBody] CancelTransferDispatchBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new CancelTransferDispatchCommand(new TransferOrderId(id), body.Reason),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ReceiveTransferAsync(
        Guid id,
        [FromBody] ReceiveTransferBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ReceiveTransferCommand(
                new TransferOrderId(id),
                [.. body.Receives.Select(r => new TransferReceiveAllocationSpec(
                    r.LineNo,
                    r.BatchId is { } batchId ? new BatchId(batchId) : null,
                    r.ReceivedQuantity,
                    r.DamagedQuantity))]),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> VerifyTransferAsync(
        Guid id,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new VerifyTransferCommand(new TransferOrderId(id)),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ResolveTransferDiscrepancyAsync(
        Guid id,
        Guid discrepancyId,
        [FromBody] ResolveTransferDiscrepancyBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ResolveTransferDiscrepancyCommand(
                new TransferDiscrepancyId(discrepancyId),
                body.Outcome,
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> GetTransferCustodyAsync(
        PosDbContext context,
        Guid id,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Transfer? transfer = await context.Transfers
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == new TransferOrderId(id), cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(TransferErrors.TransferUnknown(new TransferOrderId(id))),
                currentUser.CorrelationId.Value);
        }

        List<TransferCustodyEventSummary> events = await context.TransferCustodyEvents
            .AsNoTracking()
            .Where(e => e.TransferOrderId == transfer.Id)
            .OrderBy(e => e.Sequence)
            .Select(e => new TransferCustodyEventSummary(
                e.Sequence,
                e.Kind.ToString(),
                e.ActorUserId.Value,
                e.OccurredAtUtc,
                e.Note))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(events);
    }

    private static async Task<IResult> DispatchAsync(
        ICommand<TransferOrderId> command,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<TransferOrderId> result = await dispatcher
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return Complete(result, currentUser);
    }

    private static IResult Complete(Result<TransferOrderId> result, ICurrentUser currentUser)
        => result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
}

/// <summary>The optional note on a review or rejection.</summary>
/// <param name="Note">The reason or review note.</param>
public sealed record TransferReviewBody(string? Note = null);