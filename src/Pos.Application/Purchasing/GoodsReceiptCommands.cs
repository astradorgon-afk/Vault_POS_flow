using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Purchasing;

namespace Pos.Application.Purchasing;

/// <summary>
/// Records a goods receipt against a purchase order: posts the received
/// quantity into the inventory ledger, reconciles the order line quantities,
/// and records any discrepancies for the supplier.
/// </summary>
/// <param name="PurchaseOrderId">The order being received against.</param>
/// <param name="Lines">The receipt lines as counted by the receiver.</param>
/// <param name="DocumentsMissing">Whether the delivery arrived without its expected documents.</param>
/// <param name="SupplierDeliveryReceiptNumber">The supplier's DR number, transcribed from their paperwork.</param>
/// <param name="SupplierOfficialReceiptNumber">The supplier's OR number, transcribed from their paperwork.</param>
/// <param name="SupplierReleasedAtUtc">When the supplier released the goods to us.</param>
public sealed record CreateGoodsReceiptCommand(
    PurchaseOrderId PurchaseOrderId,
    IReadOnlyList<GoodsReceiptLineSpec> Lines,
    bool DocumentsMissing = false,
    string? SupplierDeliveryReceiptNumber = null,
    string? SupplierOfficialReceiptNumber = null,
    DateTimeOffset? SupplierReleasedAtUtc = null)
    : ICommand<GoodsReceiptId>, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Purchasing.Receive;
}