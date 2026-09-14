using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;

namespace Pos.Application.Sales;

/// <summary>
/// Voids a completed sale (POS.md §4.1). A sale can only be voided against the
/// same location, device, shift and business date it was completed on, and a
/// reason is recorded for investigation. The handler reverses the original
/// PosSale ledger movement under the same <see cref="EventId"/>, so a retried
/// void replays instead of double-posting.
/// </summary>
/// <param name="EventId">The event identifier that makes the void idempotent.</param>
/// <param name="SaleId">The sale to void.</param>
/// <param name="LocationId">The location the void happens at; must match the sale's.</param>
/// <param name="ShiftId">The open shift the void happens in; must match the sale's.</param>
/// <param name="DeviceId">The device the void happens on; must match the sale's.</param>
/// <param name="BusinessDate">The business date the void counts toward; must match the sale's.</param>
/// <param name="VoidedByUserId">The user who authorised the void.</param>
/// <param name="VoidedAtUtc">When the void happened.</param>
/// <param name="Reason">Why the sale is being voided.</param>
public sealed record VoidSaleCommand(
    EventId EventId,
    SaleId SaleId,
    LocationId LocationId,
    CashierShiftId ShiftId,
    DeviceId DeviceId,
    DateOnly BusinessDate,
    UserId VoidedByUserId,
    DateTimeOffset VoidedAtUtc,
    string Reason)
    : ICommand<SaleId>, IIdempotentCommand, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.Void;
}