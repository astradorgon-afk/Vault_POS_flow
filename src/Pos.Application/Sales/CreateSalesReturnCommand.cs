using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// Accepts a customer return against a completed sale (POS.md §4.1): goods
/// accepted back from the customer, posted from EXT-CUSTOMER's External bucket
/// into the store's ReturnPending bucket under the same document and event.
/// The RET number is allocated on the device when the return starts
/// (DocumentNumber.CreateForDevice), so a return marched offline keeps the
/// number that will print on its credit receipt when it syncs. The server
/// verifies the number's device code against the authenticated device and
/// enforces every cap itself — the sale must still be completed, each line may
/// be returned at most up to the quantity originally sold, and the return's
/// value is what later refunds may draw against.
/// </summary>
/// <remarks>
/// <para>
/// The device resolves the lines being returned as product-and-quantity; the
/// server matches them against the sale's lines first come, first served. A
/// sale line split across batches returns the earliest batch slices first, so
/// the snapshots always land on the units that were actually bought.
/// </para>
/// <para>
/// The return window — commonly seven days for a full refund — is enforced by
/// the device as a usability gate, not by the server: the server's insistence
/// is structural (the sale exists, is completed, is not voided and still holds
/// returnable units), never a hard-coded day count that would overrule what a
/// store configures.
/// </para>
/// </remarks>
/// <param name="Number">The device-allocated RET number, for example <c>RET-2026-D03-000005</c>.</param>
/// <param name="EventId">The business event, generated on the device.</param>
/// <param name="SaleId">The sale the goods were bought under.</param>
/// <param name="LocationId">The location the return happens at; must match the sale's.</param>
/// <param name="ShiftId">The open cashier shift the return happens in.</param>
/// <param name="DeviceId">The device that accepts the return.</param>
/// <param name="CustomerId">The account customer, when the original sale was charged to one.</param>
/// <param name="BusinessDate">The business date the return counts toward.</param>
/// <param name="ReturnedAtUtc">When the goods were accepted back, by the device clock.</param>
/// <param name="ReturnedByUserId">The user who accepts the goods back.</param>
/// <param name="Lines">The lines being returned.</param>
public sealed record CreateSalesReturnCommand(
    DocumentNumber Number,
    EventId EventId,
    SaleId SaleId,
    LocationId LocationId,
    CashierShiftId ShiftId,
    DeviceId DeviceId,
    CustomerId? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset ReturnedAtUtc,
    UserId ReturnedByUserId,
    IReadOnlyList<SalesReturnLine> Lines)
    : ICommand<SalesReturnId>, IIdempotentCommand, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.Return;
}

/// <summary>
/// One line of a customer return. The server matches the product against the
/// sale's lines first come, first served and re-derives every value from the
/// sale line snapshots; the line only says what is coming back and how many.
/// </summary>
/// <param name="ProductId">The product accepted back.</param>
/// <param name="Quantity">The quantity accepted back, in the unit of measure.</param>
public sealed record SalesReturnLine(ProductId ProductId, decimal Quantity);