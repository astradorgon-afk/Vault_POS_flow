using Pos.Domain.Common;
using Pos.Domain.Locations;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Application.Sales;

/// <summary>
/// Persists cashier shifts. Mutations opt into tracking explicitly because the
/// context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
public interface IShiftRepository
{
    /// <summary>Stages a newly opened shift on the context.</summary>
    /// <param name="shift">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted identifier.</returns>
    Task<Result<CashierShiftId>> AddAsync(CashierShift shift, CancellationToken cancellationToken);

    /// <summary>Loads a shift.</summary>
    /// <param name="shiftId">The shift identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shift, or <see langword="null"/> when it does not exist.</returns>
    Task<CashierShift?> GetShiftAsync(CashierShiftId shiftId, CancellationToken cancellationToken);

    /// <summary>Loads the open shift for a device, if any.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The open shift, or <see langword="null"/> when the device has none.</returns>
    Task<CashierShift?> GetOpenShiftForDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Stages a closed shift update on the context.</summary>
    /// <param name="shift">The aggregate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shift identifier.</returns>
    Task<Result<CashierShiftId>> UpdateAsync(CashierShift shift, CancellationToken cancellationToken);

    /// <summary>Loads a location's kind and settings for the open-time checks.</summary>
    /// <param name="locationId">The location.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The location facts, or <see langword="null"/> when the location does not exist.</returns>
    Task<ShiftLocationFacts?> GetLocationFactsAsync(LocationId locationId, CancellationToken cancellationToken);

    /// <summary>Loads the device facts needed to validate a device-scoped number.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The device facts, or <see langword="null"/> when the device does not exist.</returns>
    Task<ShiftDeviceFacts?> GetDeviceFactsAsync(DeviceId deviceId, CancellationToken cancellationToken);

    /// <summary>Aggregates the cash totals a shift closure reconciles against.</summary>
    /// <param name="shiftId">The shift.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The totals; payouts are zero until a later batch adds them.</returns>
    Task<ShiftCashTotals> GetShiftCashTotalsAsync(CashierShiftId shiftId, CancellationToken cancellationToken);

    /// <summary>
    /// Aggregates, per payment method, everything already refunded against a
    /// sale across every return of it. A refund may never push the sum past
    /// what the sale was originally paid by that method.
    /// </summary>
    /// <param name="saleId">The sale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-method refunded amounts, keyed by method.</returns>
    Task<IReadOnlyDictionary<PaymentMethod, decimal>> GetRefundedAmountsByMethodAsync(
        SaleId saleId,
        CancellationToken cancellationToken);
}

/// <summary>What a shift handler needs to know about one location.</summary>
/// <param name="Kind">The location kind, used to refuse external locations.</param>
/// <param name="Settings">The location's operational settings.</param>
public sealed record ShiftLocationFacts(LocationKind Kind, LocationSettings Settings);

/// <summary>What a shift handler needs to know about one device.</summary>
/// <param name="ShortCode">The short code embedded in the device's offline numbers.</param>
public sealed record ShiftDeviceFacts(string ShortCode);

/// <summary>The cash a shift closure reconciles against.</summary>
/// <param name="CashSales">Total cash received for sales during the shift.</param>
/// <param name="CashRefunds">Total cash paid out as refunds during the shift.</param>
/// <param name="Payouts">Total petty-cash payouts during the shift.</param>
public sealed record ShiftCashTotals(decimal CashSales, decimal CashRefunds, decimal Payouts);