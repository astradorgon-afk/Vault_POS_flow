using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>
/// Issues a standing authorization for a supplier to deliver goods directly to
/// a store, scoped to a date window and optionally a product and value cap.
/// </summary>
/// <param name="SupplierId">The supplier allowed to deliver.</param>
/// <param name="StoreLocationId">The store that may receive the delivery.</param>
/// <param name="ValidFrom">The first day the authorization applies.</param>
/// <param name="ValidUntil">The last day the authorization applies.</param>
/// <param name="ProductId">An optional product the authorization is limited to.</param>
/// <param name="ValueCap">An optional cap on the delivered value.</param>
public sealed record CreateDirectDeliveryAuthorizationCommand(
    SupplierId SupplierId,
    LocationId StoreLocationId,
    DateOnly ValidFrom,
    DateOnly ValidUntil,
    ProductId? ProductId = null,
    decimal? ValueCap = null)
    : ICommand<DirectDeliveryAuthorizationId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.AuthorizeDirectToStore;
}

/// <summary>Withdraws a direct-delivery authorization.</summary>
/// <param name="AuthorizationId">The authorization to revoke.</param>
public sealed record RevokeDirectDeliveryAuthorizationCommand(DirectDeliveryAuthorizationId AuthorizationId)
    : ICommand<DirectDeliveryAuthorizationId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.AuthorizeDirectToStore;
}

/// <summary>
/// Raises a supplier return: a document under which unsellable stock moves from
/// Damaged, Expired or Quarantine back to the supplier.
/// </summary>
/// <param name="SupplierId">The supplier receiving the goods back.</param>
/// <param name="LocationId">The location the stock sits in.</param>
/// <param name="Lines">The lines to return, in the products' base units.</param>
public sealed record CreateSupplierReturnCommand(
    SupplierId SupplierId,
    LocationId LocationId,
    IReadOnlyList<SupplierReturnLineSpec> Lines)
    : ICommand<SupplierReturnId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Return;
}

/// <summary>Submits a supplier return for approval.</summary>
/// <param name="ReturnId">The return.</param>
public sealed record SubmitSupplierReturnCommand(SupplierReturnId ReturnId)
    : ICommand<SupplierReturnId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Return;
}

/// <summary>Approves a submitted supplier return, subject to the value tier.</summary>
/// <param name="ReturnId">The return.</param>
public sealed record ApproveSupplierReturnCommand(SupplierReturnId ReturnId)
    : ICommand<SupplierReturnId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Approve;
}

/// <summary>Rejects a submitted supplier return, sending it back to draft.</summary>
/// <param name="ReturnId">The return.</param>
public sealed record RejectSupplierReturnCommand(SupplierReturnId ReturnId)
    : ICommand<SupplierReturnId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Approve;
}

/// <summary>
/// Dispatches an approved return: allocates the SRT number and posts the ledger
/// group moving the stock to the EXT-SUPPLIER counterparty.
/// </summary>
/// <param name="ReturnId">The return.</param>
/// <param name="SupplierAuthorizationNumber">The supplier's return-authorization
/// number for the dispatch.</param>
public sealed record DispatchSupplierReturnCommand(
    SupplierReturnId ReturnId,
    string SupplierAuthorizationNumber)
    : ICommand<SupplierReturnId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Return;
}

/// <summary>Records that the supplier confirmed the return.</summary>
/// <param name="ReturnId">The return.</param>
public sealed record ConfirmSupplierReturnCommand(SupplierReturnId ReturnId)
    : ICommand<SupplierReturnId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Return;
}

/// <summary>
/// Closes a receiving discrepancy with a resolution decision. The follow-up
/// (credit issued, replacement arrives, write-off approved) is executed through
/// its own flow; this action records the outcome.
/// </summary>
/// <param name="DiscrepancyId">The discrepancy to resolve.</param>
/// <param name="Outcome">The closing decision.</param>
/// <param name="Note">An optional free-text note.</param>
public sealed record ResolveReceivingDiscrepancyCommand(
    ReceivingDiscrepancyId DiscrepancyId,
    ReceivingDiscrepancyResolutionOutcome Outcome,
    string? Note = null)
    : ICommand<GoodsReceiptId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.ResolveDiscrepancy;
}