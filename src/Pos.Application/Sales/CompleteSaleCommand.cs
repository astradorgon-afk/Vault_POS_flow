using Pos.Application.Common.Messaging;
using Pos.Application.Identity;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Completes a point-of-sale transaction: the cashier's record of what was
/// sold, for how much, and how it was paid (POS.md §3). A sale moves goods off
/// the shelf in the same transaction that stores it, posts the inventory
/// movements and writes the audit entry.
/// </summary>
/// <remarks>
/// The device already resolved prices, discounts and batches when the sale was
/// rung up; the server re-derives every fact authoritatively — the price rows,
/// VAT classification, FEFO allocation and cash rounding — so the recorded
/// sale never trusts the client for its numbers. The event identifier makes a
/// retried completion harmless: the ledger deduplicates the event and the sale
/// is written once.
/// </remarks>
/// <param name="EventId">The business event, generated on the device.</param>
/// <param name="LocationId">The location the sale happened at.</param>
/// <param name="CashierShiftId">The shift the sale belongs to.</param>
/// <param name="DeviceId">The device that completed the sale.</param>
/// <param name="CashierId">The cashier who completed the sale.</param>
/// <param name="CustomerId">The account customer the sale was charged to, when any.</param>
/// <param name="BusinessDate">The business date the sale counts toward.</param>
/// <param name="CompletedAtUtc">When the sale was completed, by the device clock.</param>
/// <param name="Lines">The lines to sell.</param>
/// <param name="Payments">The payments that settle the sale.</param>
public sealed record CompleteSaleCommand(
    EventId EventId,
    LocationId LocationId,
    CashierShiftId CashierShiftId,
    DeviceId DeviceId,
    UserId CashierId,
    CustomerId? CustomerId,
    DateOnly BusinessDate,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<CompleteSaleLine> Lines,
    IReadOnlyList<CompleteSalePayment> Payments)
    : ICommand<SaleId>, IIdempotentCommand, ILocationScoped, IAuthorizedMessage
{
    /// <inheritdoc />
    public string RequiredPermission => Permissions.Sales.Create;
}

/// <summary>
/// One line of a completed sale. The server re-resolves the price, VAT class
/// and batch; the line only says what was sold and which facts were overridden
/// on the device.
/// </summary>
/// <param name="ProductId">The product sold.</param>
/// <param name="Quantity">The quantity sold, in the unit of measure.</param>
/// <param name="UnitOfMeasureId">The unit of measure the quantity is expressed in.</param>
/// <param name="Barcode">The scanned barcode, when the product was scanned.</param>
/// <param name="UnitPriceOverride">The cashier-entered price, when the resolved price was overridden.</param>
/// <param name="PriceOverrideAuthorizedByUserId">Who authorised the override; required when <paramref name="UnitPriceOverride"/> is set.</param>
/// <param name="Discount">The manual discount applied to the line.</param>
/// <param name="DiscountAuthorizedByUserId">Who authorised the discount; required when <paramref name="Discount"/> is positive.</param>
/// <param name="AllowExpiredOverride">Whether an expired batch may cover the line when sellable stock runs short.</param>
public sealed record CompleteSaleLine(
    ProductId ProductId,
    decimal Quantity,
    UnitOfMeasureId UnitOfMeasureId,
    string? Barcode,
    decimal? UnitPriceOverride,
    UserId? PriceOverrideAuthorizedByUserId,
    decimal Discount,
    UserId? DiscountAuthorizedByUserId,
    bool AllowExpiredOverride);

/// <summary>
/// One payment that settles a sale.
/// </summary>
/// <param name="Method">The payment method.</param>
/// <param name="Amount">The amount applied to the sale.</param>
/// <param name="Tendered">The amount tendered; required for cash.</param>
/// <param name="ProviderReference">The provider reference for card and wallet payments.</param>
public sealed record CompleteSalePayment(
    PaymentMethod Method,
    decimal Amount,
    decimal? Tendered,
    string? ProviderReference);