using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Receipts;

namespace Pos.Application.Receipts;

/// <summary>
/// Issues a payment receipt: money taken in or paid out, recorded as an
/// RCT-numbered document. Receipts are standalone records of a cash event; they
/// carry no ledger entry and no resulting balance (ADR-0026). The handler
/// allocates the RCT number, so the counter advances exactly once, in the same
/// transaction as the receipt.
/// </summary>
/// <param name="Kind">What the receipt documents.</param>
/// <param name="LocationId">The branch the cash event happened at.</param>
/// <param name="Amount">The amount recorded, greater than zero.</param>
/// <param name="Counterparty">Optional counterparty name, for example a customer or supplier.</param>
/// <param name="Note">Optional purpose note.</param>
/// <param name="ReferenceNumber">Optional number of a related document, such as a sale.</param>
public sealed record IssueReceiptCommand(
    ReceiptKind Kind,
    LocationId LocationId,
    decimal Amount,
    string? Counterparty = null,
    string? Note = null,
    string? ReferenceNumber = null)
    : ICommand<ReceiptId>, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Receipts.Create;
}