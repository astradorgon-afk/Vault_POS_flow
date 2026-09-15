using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// Accepts a blind customer return (POS.md §4): goods accepted back with no
/// original sale, under <c>sale.return_blind</c>. The ledger effect is the same
/// as a referenced return — EXT-CUSTOMER's External bucket gives up the goods
/// and the store's ReturnPending bucket receives them — plus an exception
/// record: the mandatory reason and the <c>sale.return.blind.accepted</c> audit
/// entry make the acceptance accountable even though no document ties it back.
/// The RET number is device-allocated like a referenced return's; the server
/// verifies the number's device code against the authenticated device and the
/// shift's device, resolves every line's price and VAT class from the catalog,
/// and persists the whole acceptance atomically with its ledger movement.
/// </summary>
/// <param name="Number">The device-allocated RET number, for example <c>RET-2026-D03-000008</c>.</param>
/// <param name="EventId">The business event, generated on the device.</param>
/// <param name="LocationId">The location the return happens at.</param>
/// <param name="ShiftId">The open cashier shift the return happens in.</param>
/// <param name="DeviceId">The device that accepts the return.</param>
/// <param name="CustomerId">The account customer, when known.</param>
/// <param name="BusinessDate">The business date the return counts toward.</param>
/// <param name="ReturnedAtUtc">When the goods were accepted back, by the device clock.</param>
/// <param name="ReturnedByUserId">The manager who accepts the goods back.</param>
/// <param name="Reason">Why a return was accepted without its original sale.</param>
/// <param name="Lines">The lines being returned.</param>
public sealed record CreateBlindSalesReturnCommand(
    DocumentNumber Number,
    EventId EventId,
    LocationId LocationId,
    CashierShiftId ShiftId,
    DeviceId DeviceId,
    CustomerId? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset ReturnedAtUtc,
    UserId ReturnedByUserId,
    string Reason,
    IReadOnlyList<BlindSalesReturnLine> Lines)
    : ICommand<SalesReturnId>, IIdempotentCommand, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.ReturnBlind;
}

/// <summary>
/// One line of a blind customer return. There is no sale line to re-derive
/// values from, so the server resolves the current price and VAT class from the
/// catalog before the aggregate freezes its snapshot; the line only says what
/// is coming back and how many.
/// </summary>
/// <param name="ProductId">The product accepted back.</param>
/// <param name="Quantity">The quantity accepted back, in the product's base unit of measure.</param>
public sealed record BlindSalesReturnLine(ProductId ProductId, decimal Quantity);