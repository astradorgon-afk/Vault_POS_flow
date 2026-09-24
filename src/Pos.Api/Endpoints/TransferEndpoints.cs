using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Authorization;
using Pos.Api.Common;
using Pos.Application.Common.Abstractions;
using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Application.Transfers;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Transfers;
using Pos.Infrastructure.Identity;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Endpoints;

/// <summary>The body of a transfer creation.</summary>
/// <param name="SourceLocationId">The location the stock leaves.</param>
/// <param name="DestinationLocationId">The location the stock is destined for.</param>
/// <param name="Lines">The lines to move, in the products' base units.</param>
/// <param name="PreApprovalTokenId">
/// The head office token the transfer is pre-approved by, when supplied.
/// </param>
public sealed record CreateTransferBody(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<CreateTransferLineBody> Lines,
    Guid? PreApprovalTokenId = null);

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

/// <summary>One line of an emergency transfer.</summary>
/// <param name="ProductId">The product to move.</param>
/// <param name="Quantity">The requested quantity, in the base unit.</param>
public sealed record EmergencyTransferLineBody(Guid ProductId, decimal Quantity);

/// <summary>The destination store manager co-signing an emergency transfer.</summary>
/// <param name="UserName">Their username or e-mail address.</param>
/// <param name="Password">Their password.</param>
public sealed record EmergencyCoAuthorizationBody(string UserName, string Password);

/// <summary>The body of an emergency transfer creation.</summary>
/// <param name="SourceLocationId">The store the stock leaves.</param>
/// <param name="DestinationLocationId">The store the stock is destined for.</param>
/// <param name="Lines">The lines to move; batch-tracked products are refused.</param>
/// <param name="CoAuthorization">The destination store manager co-signing the movement.</param>
/// <param name="Note">Why the movement is an emergency; recorded on the ledger.</param>
public sealed record InitiateEmergencyBody(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<EmergencyTransferLineBody> Lines,
    EmergencyCoAuthorizationBody CoAuthorization,
    string Note);

/// <summary>The body of a central review decision.</summary>
/// <param name="Approve">Whether to ratify or reject.</param>
/// <param name="Note">Required when rejecting; recorded on the reversal's ledger group.</param>
public sealed record CentralReviewBody(bool Approve, string? Note = null);

/// <summary>The body of a pre-approval token issue.</summary>
/// <param name="SourceLocationId">The store the covered transfers leave.</param>
/// <param name="DestinationLocationId">The store the covered transfers arrive at.</param>
/// <param name="Products">The covered products; empty means every stock product.</param>
/// <param name="MaxValue">The value ceiling of one covered transfer, or null.</param>
/// <param name="ValidFromUtc">The instant the token becomes usable.</param>
/// <param name="ValidUntilUtc">The instant the token expires.</param>
/// <param name="Note">An optional note explaining why the token was issued.</param>
public sealed record IssuePreApprovalTokenBody(
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<Guid> Products,
    decimal? MaxValue,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidUntilUtc,
    string? Note = null);

/// <summary>A pre-approval token as it appears in a list.</summary>
public sealed record PreApprovalTokenSummary(
    Guid Id,
    string Number,
    string Status,
    Guid SourceLocationId,
    Guid DestinationLocationId,
    IReadOnlyList<Guid> Products,
    decimal? MaxValue,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidUntilUtc,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    Guid? ConsumedByTransferId,
    DateTimeOffset? ConsumedAtUtc);

/// <summary>The body of a token revocation.</summary>
/// <param name="Reason">Why the token is withdrawn.</param>
public sealed record RevokePreApprovalTokenBody(string Reason);

/// <summary>One replenishment recommendation for a stocked product at a location.</summary>
/// <param name="LocationId">The store needing stock.</param>
/// <param name="ProductId">The product running low.</param>
/// <param name="Available">The sellable quantity on hand.</param>
/// <param name="Deficit">How far below the target the location is.</param>
/// <param name="Urgency">Critical, High or Normal based on the thresholds.</param>
/// <param name="SuggestedSourceLocationId">The suggested source of the stock, when one exists.</param>
/// <param name="SuggestedQuantity">How much to request, bounded by the source surplus and preference.</param>
public sealed record ReplenishmentRecommendation(
    Guid LocationId,
    Guid ProductId,
    decimal Available,
    decimal Deficit,
    string Urgency,
    Guid? SuggestedSourceLocationId,
    decimal SuggestedQuantity);

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

/// <summary>A transfer's detail: its lines, picked allocations and arrival state.</summary>
public sealed record TransferDetailView(
    TransferSummary Transfer,
    string Kind,
    string Mode,
    string? ReviewNote,
    IReadOnlyList<TransferLineView> Lines);

/// <summary>One line of a transfer detail.</summary>
public sealed record TransferLineView(
    int LineNo,
    Guid ProductId,
    string? ProductName,
    decimal RequestedQuantity,
    decimal PickedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity,
    string? Note,
    string? DiscrepancyState,
    IReadOnlyList<TransferAllocationView> Allocations);

/// <summary>One picked lot within a transfer line.</summary>
public sealed record TransferAllocationView(
    Guid? BatchId,
    decimal PickedQuantity,
    decimal ReceivedQuantity,
    decimal DamagedQuantity);

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

        group.MapGet("/{id:guid}", GetTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetTransfer")
            .WithSummary("Gets one transfer with its lines, allocations and arrival state.");

        group.MapGet("/{id:guid}/custody", GetTransferCustodyAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.View)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetTransferCustody")
            .WithSummary("Gets a transfer's custody timeline.");

        group.MapPost("/emergency", InitiateEmergencyTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Emergency)
            {
                Scope = ScopeSource.None,
            })
            .WithName("InitiateEmergencyTransfer")
            .WithSummary("Creates an emergency store-to-store transfer under two managers' co-signature.");

        group.MapGet("/pending-central-review", GetPendingCentralReviewAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetPendingCentralReview")
            .WithSummary("Lists emergency transfers awaiting central review.");

        group.MapPost("/{id:guid}/central-review", ReviewCentralTransferAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Approve)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ReviewCentralTransfer")
            .WithSummary("Ratifies or rejects a pending emergency transfer.");

        RouteGroupBuilder preApprovals = app.MapGroup("/api/v1/pre-approvals").WithTags("Pre-Approvals");

        preApprovals.MapPost("/", IssuePreApprovalTokenAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.IssuePreApproval)
            {
                Scope = ScopeSource.None,
            })
            .WithName("IssuePreApprovalToken")
            .WithSummary("Issues a single-use pre-approval token.");

        preApprovals.MapGet("/", ListPreApprovalTokensAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.IssuePreApproval)
            {
                Scope = ScopeSource.None,
            })
            .WithName("ListPreApprovalTokens")
            .WithSummary("Lists pre-approval tokens.");

        preApprovals.MapPost("/{id:guid}/revoke", RevokePreApprovalTokenAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.IssuePreApproval)
            {
                Scope = ScopeSource.None,
            })
            .WithName("RevokePreApprovalToken")
            .WithSummary("Revokes a pre-approval token before it is used.");

        RouteGroupBuilder replenishment = app.MapGroup("/api/v1/replenishment").WithTags("Replenishment");

        replenishment.MapGet("/recommendations", GetReplenishmentRecommendationsAsync)
            .WithMetadata(new RequirePermissionAttribute(Permissions.Transfer.Request)
            {
                Scope = ScopeSource.None,
            })
            .WithName("GetReplenishmentRecommendations")
            .WithSummary("Recommends replenishment quantities for the caller's stocked locations.");

        return app;
    }

    private static async Task<IResult> ListTransfersAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        IQueryable<Transfer> query = context.Transfers.AsNoTracking();

        // The list is scoped to the caller's locations unless they act
        // business-wide: a store manager only sees transfers into and out of
        // their own store.
        if (!authorization.HasAllLocations)
        {
            query = query.Where(t =>
                authorization.Locations.Contains(t.SourceLocationId)
                || authorization.Locations.Contains(t.DestinationLocationId));
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
                        l.Note))],
                    body.PreApprovalTokenId is null
                        ? null
                        : new PreApprovalTokenId(body.PreApprovalTokenId.Value)),
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

    private static async Task<IResult> GetTransferAsync(
        PosDbContext context,
        DatabasePermissionEvaluator evaluator,
        Guid id,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        TransferOrderId transferId = new(id);

        Transfer? transfer = await context.Transfers
            .AsNoTracking()
            .Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == transferId, cancellationToken)
            .ConfigureAwait(false);

        if (transfer is null)
        {
            return ProblemDetailsMapping.ToProblem(
                Result.Failure(TransferErrors.TransferUnknown(transferId)),
                currentUser.CorrelationId.Value);
        }

        // The detail is scoped like the list: a store manager only sees
        // transfers into and out of their own stores. Out-of-scope reads and
        // unknown orders are indistinguishable on purpose.
        UserAuthorization authorization = await evaluator
            .GetAuthorizationAsync(currentUser.UserId ?? UserId.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (!authorization.HasAllLocations)
        {
            if (!authorization.Locations.Contains(transfer.SourceLocationId)
                && !authorization.Locations.Contains(transfer.DestinationLocationId))
            {
                return ProblemDetailsMapping.ToProblem(
                    Result.Failure(TransferErrors.TransferUnknown(transferId)),
                    currentUser.CorrelationId.Value);
            }
        }

        List<TransferPickAllocation> allocations = await context.TransferAllocations
            .AsNoTracking()
            .Where(a => a.TransferOrderId == transferId)
            .OrderBy(a => a.LineNo)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<TransferDiscrepancy> discrepancies = await context.TransferDiscrepancies
            .AsNoTracking()
            .Where(d => d.TransferOrderId == transferId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        ProductId[] productIds = [.. transfer.Lines.Select(l => l.ProductId).Distinct()];

        Dictionary<Guid, string> names = await context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id.Value, p => p.Name, cancellationToken)
            .ConfigureAwait(false);

        List<TransferLineView> lines = [];

        foreach (TransferLine line in transfer.Lines.OrderBy(l => l.LineNo))
        {
            List<TransferPickAllocation> lineAllocations = allocations
                .Where(a => a.LineNo == line.LineNo)
                .ToList();

            string? discrepancyState = discrepancies
                .Where(d => d.LineNo == line.LineNo && !d.IsResolved)
                .Select(d => d.Kind.ToString())
                .FirstOrDefault();

            lines.Add(new TransferLineView(
                line.LineNo,
                line.ProductId.Value,
                names.GetValueOrDefault(line.ProductId.Value),
                line.RequestedQuantity,
                lineAllocations.Sum(a => a.Quantity),
                lineAllocations.Sum(a => a.ReceivedQuantity),
                lineAllocations.Sum(a => a.DamagedQuantity),
                line.Note,
                discrepancyState,
                lineAllocations
                    .Select(a => new TransferAllocationView(
                        a.BatchId?.Value,
                        a.Quantity,
                        a.ReceivedQuantity,
                        a.DamagedQuantity))
                    .ToList()));
        }

        TransferDetailView detail = new(
            new TransferSummary(
                transfer.Id.Value,
                transfer.Number,
                transfer.Status.ToString(),
                transfer.SourceLocationId.Value,
                transfer.DestinationLocationId.Value,
                transfer.CreatedByUserId.Value,
                transfer.CreatedAtUtc,
                transfer.DispatchedAtUtc,
                transfer.ReceivedAtUtc,
                transfer.TotalValue,
                transfer.Lines.Count),
            transfer.Kind.ToString(),
            transfer.Mode.ToString(),
            transfer.ReviewNote,
            lines);

        return TypedResults.Ok(detail);
    }

    private static async Task<IResult> InitiateEmergencyTransferAsync(
        [FromBody] InitiateEmergencyBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new InitiateEmergencyTransferCommand(
                new LocationId(body.SourceLocationId),
                new LocationId(body.DestinationLocationId),
                [.. body.Lines.Select(l => new TransferLineSpec(
                    new ProductId(l.ProductId),
                    l.Quantity,
                    null))],
                new EmergencyCoAuthorization(body.CoAuthorization.UserName, body.CoAuthorization.Password),
                body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> GetPendingCentralReviewAsync(
        PosDbContext context,
        CancellationToken cancellationToken)
    {
        List<TransferSummary> summaries = await context.Transfers
            .AsNoTracking()
            .Where(t => t.Status == TransferStatus.PendingCentralReview)
            .OrderBy(t => t.CreatedAtUtc)
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

    private static async Task<IResult> ReviewCentralTransferAsync(
        Guid id,
        [FromBody] CentralReviewBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
        => await DispatchAsync(
            new ReviewCentralTransferCommand(new TransferOrderId(id), body.Approve, body.Note),
            dispatcher,
            currentUser,
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> IssuePreApprovalTokenAsync(
        [FromBody] IssuePreApprovalTokenBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<PreApprovalTokenId> result = await dispatcher
            .SendAsync(
                new IssuePreApprovalTokenCommand(
                    new LocationId(body.SourceLocationId),
                    new LocationId(body.DestinationLocationId),
                    [.. body.Products.Select(p => new ProductId(p))],
                    body.MaxValue,
                    body.ValidFromUtc,
                    body.ValidUntilUtc,
                    body.Note),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> ListPreApprovalTokensAsync(
        PosDbContext context,
        CancellationToken cancellationToken)
    {
        List<PreApprovalTokenSummary> tokens = await context.PreApprovalTokens
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new PreApprovalTokenSummary(
                t.Id.Value,
                t.Number,
                t.Status.ToString(),
                t.SourceLocationId.Value,
                t.DestinationLocationId.Value,
                t.ProductRows.Select(p => p.ProductId.Value).ToList(),
                t.MaxValue,
                t.ValidFromUtc,
                t.ValidUntilUtc,
                t.CreatedByUserId.Value,
                t.CreatedAtUtc,
                t.ConsumedByTransferId != null ? t.ConsumedByTransferId.Value.Value : null,
                t.ConsumedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(tokens);
    }

    private static async Task<IResult> RevokePreApprovalTokenAsync(
        Guid id,
        [FromBody] RevokePreApprovalTokenBody body,
        IDispatcher dispatcher,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        Result<PreApprovalTokenId> result = await dispatcher
            .SendAsync(new RevokePreApprovalTokenCommand(new PreApprovalTokenId(id), body.Reason), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(new { id = result.Value.Value })
            : ProblemDetailsMapping.ToProblem(result, currentUser.CorrelationId.Value);
    }

    private static async Task<IResult> GetReplenishmentRecommendationsAsync(
        PosDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return TypedResults.Ok(Array.Empty<ReplenishmentRecommendation>());
        }

        // Scoping is assignments-based, matching the rest of the API: a user
        // sees recommendations for the stores they are assigned to, whatever
        // their role grants. The token only carries the primary location, so
        // the full assignment set comes from the database.
        List<LocationId> assignedLocations = await context.UserLocations
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.LocationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<Location> stores = await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind == LocationKind.Store && assignedLocations.Contains(l.Id))
            .OrderBy(l => l.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stores.Count == 0)
        {
            return TypedResults.Ok(Array.Empty<ReplenishmentRecommendation>());
        }

        List<Location> sources = await context.Locations
            .AsNoTracking()
            .Where(l => l.Kind != LocationKind.External)
            .OrderBy(l => l.Kind == LocationKind.MainWarehouse ? 0 : 1)
            .ThenBy(l => l.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<LocationId> storeIds = stores.Select(s => s.Id).ToList();
        List<LocationId> sourceIds = sources.Select(s => s.Id).ToList();

        List<InventoryBalance> balances = await context.InventoryBalances
            .AsNoTracking()
            .Where(b => b.State == InventoryState.Available
                && (storeIds.Contains(b.LocationId) || sourceIds.Contains(b.LocationId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(Guid LocationId, Guid ProductId), decimal> availableByLocationProduct = [];
        foreach (InventoryBalance balance in balances)
        {
            (Guid, Guid) key = (balance.LocationId.Value, balance.ProductId.Value);
            availableByLocationProduct[key] = availableByLocationProduct.GetValueOrDefault(key) + balance.Quantity;
        }

        List<ProductLocationSetting> settings = await context.ProductLocationSettings
            .AsNoTracking()
            .Where(s => s.IsStocked && storeIds.Contains(s.LocationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<(Guid LocationId, Guid ProductId), decimal> minimumByLocationProduct = [];
        foreach (ProductLocationSetting s in settings)
        {
            minimumByLocationProduct[(s.LocationId.Value, s.ProductId.Value)] = s.MinimumStock;
        }

        List<Location> mainWarehouses = sources
            .Where(s => s.Kind == LocationKind.MainWarehouse)
            .ToList();

        List<ReplenishmentRecommendation> recommendations = [];

        foreach (ProductLocationSetting setting in settings
            .Where(s => availableByLocationProduct.GetValueOrDefault((s.LocationId.Value, s.ProductId.Value))
                < s.ReorderPoint)
            .OrderBy(s => s.LocationId)
            .ThenBy(s => s.ProductId))
        {
            decimal onHand = availableByLocationProduct.GetValueOrDefault(
                (setting.LocationId.Value, setting.ProductId.Value));
            decimal deficit = Math.Max(0m, setting.TargetStock - onHand);
            if (deficit <= 0m)
            {
                continue;
            }

            string urgency = onHand < setting.MinimumStock
                ? "Critical"
                : onHand <= setting.ReorderPoint ? "High" : "Normal";

            // The Main Warehouse holding the largest surplus is the preferred
            // source; otherwise the store with the largest excess above its own
            // minimum.
            Guid? sourceId = null;
            decimal sourceSurplus = 0m;

            foreach (Location warehouse in mainWarehouses)
            {
                decimal surplus = Math.Max(
                    0m,
                    availableByLocationProduct.GetValueOrDefault((warehouse.Id.Value, setting.ProductId.Value)));
                if (surplus > sourceSurplus)
                {
                    sourceSurplus = surplus;
                    sourceId = warehouse.Id.Value;
                }
            }

            if (sourceId is null)
            {
                foreach (Location store in stores.Where(s => s.Id != setting.LocationId))
                {
                    decimal minimum = minimumByLocationProduct.GetValueOrDefault(
                        (store.Id.Value, setting.ProductId.Value));
                    decimal surplus = Math.Max(
                        0m,
                        availableByLocationProduct.GetValueOrDefault((store.Id.Value, setting.ProductId.Value))
                            - minimum);
                    if (surplus > sourceSurplus)
                    {
                        sourceSurplus = surplus;
                        sourceId = store.Id.Value;
                    }
                }
            }

            decimal preferred = setting.PreferredReplenishmentQuantity > 0m
                ? setting.PreferredReplenishmentQuantity
                : decimal.MaxValue;
            decimal quantity = sourceId is null
                ? 0m
                : Math.Min(deficit, Math.Min(sourceSurplus, preferred));

            recommendations.Add(new ReplenishmentRecommendation(
                setting.LocationId.Value,
                setting.ProductId.Value,
                onHand,
                deficit,
                urgency,
                sourceId,
                quantity));
        }

        return TypedResults.Ok(recommendations);
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