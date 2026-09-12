using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>Creates a draft purchase order. No document number is allocated yet.</summary>
/// <param name="SupplierId">The supplier.</param>
/// <param name="DestinationLocationId">The location that will receive the goods.</param>
/// <param name="Lines">The ordered lines; quantities are in the product's base unit.</param>
/// <param name="CurrencyCode">Three-letter ISO-4217 currency code; defaults to PHP.</param>
/// <param name="ExpectedAtUtc">Expected delivery instant, or null.</param>
public sealed record CreatePurchaseOrderCommand(
    SupplierId SupplierId,
    LocationId DestinationLocationId,
    IReadOnlyList<PurchaseOrderLineSpec> Lines,
    string? CurrencyCode = null,
    DateTimeOffset? ExpectedAtUtc = null)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Create;
}

/// <summary>Submits a draft for approval, allocating its document number.</summary>
/// <param name="OrderId">The draft.</param>
public sealed record SubmitPurchaseOrderCommand(PurchaseOrderId OrderId)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Create;
}

/// <summary>Approves a submitted purchase order against the approver's tier.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Notes">Optional notes attached to the decision.</param>
public sealed record ApprovePurchaseOrderCommand(PurchaseOrderId OrderId, string? Notes = null)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Approve;
}

/// <summary>Rejects a submitted purchase order; the order is frozen.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Notes">Optional notes attached to the decision.</param>
public sealed record RejectPurchaseOrderCommand(PurchaseOrderId OrderId, string? Notes = null)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Approve;
}

/// <summary>Marks an approved order as sent to the supplier.</summary>
/// <param name="OrderId">The order.</param>
public sealed record SendPurchaseOrderCommand(PurchaseOrderId OrderId)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Approve;
}

/// <summary>Cancels a submitted purchase order; a reason is required.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Reason">Why the order is being cancelled.</param>
public sealed record CancelPurchaseOrderCommand(PurchaseOrderId OrderId, string? Reason = null)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Approve;
}

/// <summary>Withdraws a draft that has never been submitted.</summary>
/// <param name="OrderId">The draft.</param>
public sealed record WithdrawPurchaseOrderCommand(PurchaseOrderId OrderId)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Create;
}

/// <summary>Closes an ordered purchase order; a reason is required unless fully received.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Reason">Why the order is being closed.</param>
public sealed record ClosePurchaseOrderCommand(PurchaseOrderId OrderId, string? Reason = null)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Approve;
}