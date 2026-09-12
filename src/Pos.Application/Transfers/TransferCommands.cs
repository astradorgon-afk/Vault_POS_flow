using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Transfers;

namespace Pos.Application.Transfers;

/// <summary>Raises a draft transfer between two internal locations.</summary>
/// <param name="SourceLocationId">The location the stock leaves.</param>
/// <param name="DestinationLocationId">The location the stock is destined for.</param>
/// <param name="Lines">The lines to move, in the products' base units.</param>
public sealed record CreateTransferCommand(
    LocationId SourceLocationId,
    LocationId DestinationLocationId,
    IReadOnlyList<TransferLineSpec> Lines)
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