using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>Raises a draft transfer between two internal locations.</summary>
/// <param name="SourceLocationId">The location the stock leaves.</param>
/// <param name="DestinationLocationId">The location the stock is destined for.</param>
/// <param name="Lines">The lines to move, in the products' base units.</param>
/// <param name="PreApprovalTokenId">
/// The head office token the transfer is pre-approved by, when supplied. The
/// handler validates its scope and value, and a tokenless transfer runs the
/// ordinary review workflow.
/// </param>
public sealed record CreateTransferCommand(
    LocationId SourceLocationId,
    LocationId DestinationLocationId,
    IReadOnlyList<TransferLineSpec> Lines,
    PreApprovalTokenId? PreApprovalTokenId = null)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Request;
}

/// <summary>Submits a draft transfer for review and approval.</summary>
/// <param name="TransferId">The transfer.</param>
public sealed record SubmitTransferCommand(TransferOrderId TransferId)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Request;
}

/// <summary>Marks a submitted transfer as being reviewed.</summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="Note">An optional review note.</param>
public sealed record ReviewTransferCommand(TransferOrderId TransferId, string? Note = null)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Approve;
}

/// <summary>
/// Approves a reviewed transfer, optionally amending requested quantities. The
/// approval gate evaluates the estimated value from product master costs, since
/// picking costs are not known until the stock is chosen.
/// </summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="Amendments">Optional approval-time quantity amendments.</param>
/// <param name="Note">An optional approval note.</param>
public sealed record ApproveTransferCommand(
    TransferOrderId TransferId,
    IReadOnlyList<TransferLineAmendment>? Amendments = null,
    string? Note = null)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Approve;
}

/// <summary>Rejects a submitted or reviewed transfer, sending it back to draft.</summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="Note">The reason for rejection.</param>
public sealed record RejectTransferCommand(TransferOrderId TransferId, string? Note = null)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Approve;
}

/// <summary>One picked lot the picking handler validates and snapshots.</summary>
/// <param name="LineNo">The transfer line being satisfied.</param>
/// <param name="BatchId">The lot, for batch-tracked products.</param>
/// <param name="Quantity">The picked quantity.</param>
public sealed record TransferPickRequestItem(
    int LineNo,
    BatchId? BatchId,
    decimal Quantity);

/// <summary>
/// Records what was picked at the source. The picking handler validates the
/// allocations against the available buckets first-expiry-first-out and
/// snapshots the unit costs itself — callers never supply costs.
/// </summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="Allocations">The picked lots and quantities.</param>
public sealed record PickTransferCommand(
    TransferOrderId TransferId,
    IReadOnlyList<TransferPickRequestItem> Allocations)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Pick;
}

/// <summary>Marks picking complete; the transfer may now be dispatched.</summary>
/// <param name="TransferId">The transfer.</param>
public sealed record ReadyTransferCommand(TransferOrderId TransferId)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Pick;
}

/// <summary>
/// Dispatches a ready transfer: allocates the TRF and SHP numbers and posts the
/// ledger group moving the stock from available to in-transit.
/// </summary>
/// <param name="TransferId">The transfer.</param>
public sealed record DispatchTransferCommand(TransferOrderId TransferId)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Dispatch;
}

/// <summary>
/// Cancels a dispatched transfer before the destination receives any of it,
/// posting the reverse ledger group. Requires both transfer.dispatch (the
/// message permission) and transfer.approve (checked by the handler) because the
/// rules engine treats the cancellation as an approved movement.
/// </summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="Reason">Why the dispatch was undone; recorded on the ledger.</param>
public sealed record CancelTransferDispatchCommand(TransferOrderId TransferId, string Reason)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Dispatch;
}

/// <summary>
/// Records what arrived at the destination, splitting the in-transit stock into
/// available, damaged and, for shortfalls, transit variance buckets. Over-receipt
/// is refused because the ledger has nothing to draw from.
/// </summary>
/// <param name="TransferId">The transfer.</param>
/// <param name="Receives">What arrived, per picked allocation.</param>
public sealed record ReceiveTransferCommand(
    TransferOrderId TransferId,
    IReadOnlyList<TransferReceiveAllocationSpec> Receives)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Receive;
}

/// <summary>Verifies and closes a fully accounted transfer.</summary>
/// <param name="TransferId">The transfer.</param>
public sealed record VerifyTransferCommand(TransferOrderId TransferId)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Verify;
}

/// <summary>
/// Resolves an arrival discrepancy: the short stock was either found at the
/// destination (returned to available) or written off to the external
/// counterparty. Both post a ledger group.
/// </summary>
/// <param name="DiscrepancyId">The discrepancy.</param>
/// <param name="Outcome">Whether the stock was found or written off.</param>
/// <param name="Note">An optional resolution note.</param>
public sealed record ResolveTransferDiscrepancyCommand(
    TransferDiscrepancyId DiscrepancyId,
    TransferDiscrepancyResolutionOutcome Outcome,
    string? Note = null)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Reconcile;
}

/// <summary>
/// Issues a single-use pre-approval token. The token lets a store raise a
/// transfer request backed by the issuer's authority instead of the review
/// queue; it is consumed atomically when the transfer is submitted.
/// </summary>
/// <param name="SourceLocationId">The store the covered transfers leave.</param>
/// <param name="DestinationLocationId">The store the covered transfers arrive at.</param>
/// <param name="Products">
/// The covered products; empty means every stock product, still scoped to the route.
/// </param>
/// <param name="MaxValue">
/// The value ceiling of a single covered transfer, or null for no ceiling.
/// </param>
/// <param name="ValidFromUtc">The instant the token becomes usable.</param>
/// <param name="ValidUntilUtc">The instant the token expires.</param>
/// <param name="Note">An optional note explaining why the token was issued.</param>
public sealed record IssuePreApprovalTokenCommand(
    LocationId SourceLocationId,
    LocationId DestinationLocationId,
    IReadOnlyList<ProductId> Products,
    decimal? MaxValue,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidUntilUtc,
    string? Note = null)
    : ICommand<PreApprovalTokenId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.IssuePreApproval;
}

/// <summary>Revokes a pre-approval token before it is used.</summary>
/// <param name="TokenId">The token.</param>
/// <param name="Reason">Why the token is withdrawn.</param>
public sealed record RevokePreApprovalTokenCommand(PreApprovalTokenId TokenId, string Reason)
    : ICommand<PreApprovalTokenId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.IssuePreApproval;
}

/// <summary>
/// A second store manager co-signing an emergency transfer. Their password is
/// verified against the same identity store as the bearer's session, and their
/// emergency permission is re-checked against the destination store.
/// </summary>
/// <param name="UserName">The co-signing manager's username or e-mail address.</param>
/// <param name="Password">The co-signing manager's password.</param>
public sealed record EmergencyCoAuthorization(string UserName, string Password);

/// <summary>
/// Creates an emergency offline transfer between two stores: stock moves
/// immediately under two managers' co-signature, the ledger posts against the
/// TRF number allocated here, and head office reviews the movement centrally.
/// </summary>
/// <param name="SourceLocationId">The store the stock leaves.</param>
/// <param name="DestinationLocationId">The store the stock is destined for.</param>
/// <param name="Lines">The lines to move; batch-tracked products are refused.</param>
/// <param name="CoAuthorization">The destination store manager co-signing the movement.</param>
/// <param name="Note">Why the movement is an emergency; recorded on the ledger.</param>
public sealed record InitiateEmergencyTransferCommand(
    LocationId SourceLocationId,
    LocationId DestinationLocationId,
    IReadOnlyList<TransferLineSpec> Lines,
    EmergencyCoAuthorization CoAuthorization,
    string Note)
    : ICommand<TransferOrderId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Emergency;

    /// <inheritdoc />
    public LocationId LocationId => SourceLocationId;
}

/// <summary>
/// Decides a pending emergency at central review. Ratifying accepts the posted
/// stock as received; rejecting cancels the transfer and reverses every posted
/// unit back to the source store.
/// </summary>
/// <param name="TransferId">The emergency transfer.</param>
/// <param name="Approve">Whether to ratify or reject.</param>
/// <param name="Note">
/// Required when rejecting; recorded on the reversal's ledger group.
/// </param>
public sealed record ReviewCentralTransferCommand(
    TransferOrderId TransferId,
    bool Approve,
    string? Note = null)
    : ICommand<TransferOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Approve;
}