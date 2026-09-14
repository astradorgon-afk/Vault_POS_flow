using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Issues a refund against a customer return (POS.md §4.2): money returned
/// through one of the methods the original sale was paid with. A refund may
/// never push the cumulative refunds of the sale — across every return of it —
/// past what the sale was originally paid by that method, and it may never
/// push this return's refunds past its refundable total. Cash refunds record
/// the amount handed back; card and wallet refunds carry the provider's
/// reference.
/// </summary>
/// <remarks>
/// <para>
/// The refund supports offline retries exactly like the return does: the
/// device-generated <see cref="EventId"/> makes the refund idempotent. A
/// retried request replays the stored outcome instead of issuing twice.
/// </para>
/// <para>
/// Only the return's own shift and the sale are cross-checked; no full-refund
/// or re-stocking decision is made here — the return already posted the goods
/// back into ReturnPending, and disposition is a later, separate action.
/// </para>
/// </remarks>
/// <param name="EventId">The event identifier that makes the refund idempotent.</param>
/// <param name="SaleId">The sale the return was made against; must match the return's.</param>
/// <param name="SalesReturnId">The return the refund is issued against.</param>
/// <param name="LocationId">The location the refund happens at; must match the return's.</param>
/// <param name="ShiftId">The open cashier shift the refund happens in.</param>
/// <param name="DeviceId">The device that issues the refund; must match the return's.</param>
/// <param name="Method">The payment method the refund is returned through.</param>
/// <param name="Amount">The amount refunded.</param>
/// <param name="Tendered">The amount handed back; required for cash.</param>
/// <param name="ProviderReference">The provider reference for card and wallet refunds.</param>
/// <param name="RefundedAtUtc">When the refund happened, by the device clock.</param>
/// <param name="RefundedByUserId">The user who issued the refund.</param>
public sealed record RefundSalesReturnCommand(
    EventId EventId,
    SaleId SaleId,
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