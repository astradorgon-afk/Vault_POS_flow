using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Issues a refund against a blind customer return (POS.md §4): money returned
/// to a customer whose goods were accepted back with no original sale. A blind
/// return has no sale, so there is no original payment mix to constrain the
/// refund against — the only cap is the return's own refundable total, and
/// (because no original payment record exists to reverse) the refund is handed
/// back as cash from the drawer.
/// </summary>
/// <remarks>
/// The refund supports offline retries exactly like a referenced refund does:
/// the device-generated <see cref="EventId"/> makes the refund idempotent. A
/// retried request replays the stored outcome instead of issuing twice.
/// </remarks>
/// <param name="EventId">The event identifier that makes the refund idempotent.</param>
/// <param name="SalesReturnId">The blind return the refund is issued against.</param>
/// <param name="LocationId">The location the refund happens at; must match the return's.</param>
/// <param name="ShiftId">The open cashier shift the refund happens in.</param>
/// <param name="DeviceId">The device that issues the refund; must match the return's.</param>
/// <param name="Method">The payment method the refund is returned through.</param>
/// <param name="Amount">The amount refunded.</param>
/// <param name="Tendered">The amount handed back; required for cash.</param>
/// <param name="ProviderReference">The provider reference for card and wallet refunds.</param>
/// <param name="RefundedAtUtc">When the refund happened, by the device clock.</param>
/// <param name="RefundedByUserId">The user who issued the refund.</param>
public sealed record RefundBlindSalesReturnCommand(
    EventId EventId,
    SalesReturnId SalesReturnId,
    LocationId LocationId,
    CashierShiftId ShiftId,
    DeviceId DeviceId,
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference,
    DateTimeOffset RefundedAtUtc,
    UserId RefundedByUserId)
    : ICommand<RefundId>, IIdempotentCommand, IAuthorizedMessage, ILocationScoped
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.Refund;
}