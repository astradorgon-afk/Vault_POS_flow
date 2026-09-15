using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// Logs a permissioned reprint of a sale receipt (POS.md §4). A reprint does not
/// change the sale, the shift, or the ledger — the device re-emits the receipt
/// from its local copy — but it is never silent: the print log entry and the
/// <c>sale.receipt.reprinted</c> audit record keep every re-emission accountable.
/// </summary>
/// <param name="SaleId">The sale whose receipt is reprinted.</param>
/// <param name="LocationId">The location the reprint happens at; must match the sale's.</param>
/// <param name="DeviceId">The device the reprint happens on; recorded on the log entry.</param>
/// <param name="Reason">Why the receipt is being reprinted.</param>
/// <param name="ReprintedAtUtc">When the reprint happened.</param>
/// <param name="ReprintedByUserId">The user who authorised the reprint.</param>
public sealed record ReprintSaleReceiptCommand(
    SaleId SaleId,
    LocationId LocationId,
    DeviceId DeviceId,
    string Reason,
    DateTimeOffset ReprintedAtUtc,
    UserId ReprintedByUserId)
    : ICommand<SaleId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.Reprint;
}