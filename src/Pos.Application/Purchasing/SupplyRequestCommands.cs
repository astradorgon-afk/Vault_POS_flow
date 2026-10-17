using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>
/// Raises a supply request: the main warehouse asking to be restocked.
/// </summary>
/// <param name="DestinationLocationId">The warehouse that wants the goods.</param>
/// <param name="Lines">What it wants, in each product's base unit.</param>
/// <param name="Note">An optional note for the owner.</param>
public sealed record CreateSupplyRequestCommand(
    LocationId DestinationLocationId,
    IReadOnlyList<SupplyRequestLineSpec> Lines,
    string? Note = null)
    : ICommand<SupplyRequestId>, IAuthorizedMessage
{
    /// <summary>
    /// Raising a request is the same right as asking the warehouse for stock;
    /// what differs is who fills it.
    /// </summary>
    public string RequiredPermission => Permissions.Transfer.Request;
}

/// <summary>One line of a supply request going onto a purchase order.</summary>
/// <param name="LineNo">The request line being ordered.</param>
/// <param name="UnitOfMeasureId">The product's base unit.</param>
/// <param name="Quantity">How much to order, which may differ from what was asked for.</param>
/// <param name="UnitCost">The agreed cost per base unit.</param>
public sealed record SupplyRequestOrderLineSpec(
    int LineNo,
    UnitOfMeasureId UnitOfMeasureId,
    decimal Quantity,
    decimal UnitCost);

/// <summary>
/// Turns part or all of a supply request into a draft purchase order against a
/// supplier, and records the order against the request.
/// </summary>
/// <param name="RequestId">The request being filled.</param>
/// <param name="SupplierId">The managed supplier, or empty when naming a new one.</param>
/// <param name="Lines">The request lines going onto this order.</param>
/// <param name="CurrencyCode">Three-letter ISO-4217 code; defaults to PHP.</param>
/// <param name="ExpectedAtUtc">Expected delivery instant, or null.</param>
/// <param name="SupplierName">Name of a new supplier, used when <paramref name="SupplierId"/> is empty.</param>
public sealed record OrderSupplyRequestCommand(
    SupplyRequestId RequestId,
    SupplierId SupplierId,
    IReadOnlyList<SupplyRequestOrderLineSpec> Lines,
    string? CurrencyCode = null,
    DateTimeOffset? ExpectedAtUtc = null,
    string? SupplierName = null)
    : ICommand<PurchaseOrderId>, IAuthorizedMessage
{
    /// <summary>Buying against a request is ordinary purchasing authority.</summary>
    public string RequiredPermission => Permissions.Purchasing.Create;
}

/// <summary>Turns a supply request down.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="Reason">Why, so the warehouse knows where it stands.</param>
public sealed record RejectSupplyRequestCommand(SupplyRequestId RequestId, string? Reason)
    : ICommand<SupplyRequestId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Create;
}

/// <summary>Withdraws a supply request before anything has been ordered for it.</summary>
/// <param name="RequestId">The request.</param>
/// <param name="Reason">Why.</param>
public sealed record CancelSupplyRequestCommand(SupplyRequestId RequestId, string? Reason)
    : ICommand<SupplyRequestId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Transfer.Request;
}
