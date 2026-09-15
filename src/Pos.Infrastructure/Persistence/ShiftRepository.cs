using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Organizations;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Persists cashier shifts, and reads the facts a shift handler verifies: the
/// location, the device's number short code, and the cash totals a closure
/// reconciles against. Mutations opt into tracking explicitly because the
/// context defaults to NoTracking; a detached change would silently save nothing.
/// </summary>
/// <param name="context">The database context.</param>
public sealed class ShiftRepository(PosDbContext context) : IShiftRepository
{
    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> AddAsync(CashierShift shift, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shift);

        context.CashierShifts.Add(shift);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<CashierShiftId>.Success(shift.Id);
    }

    /// <inheritdoc />
    public Task<CashierShift?> GetShiftAsync(CashierShiftId shiftId, CancellationToken cancellationToken)
        => context.CashierShifts
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shiftId, cancellationToken);

    /// <inheritdoc />
    public Task<CashierShift?> GetOpenShiftForDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken)
        => context.CashierShifts
            .AsNoTracking()
            .Where(s => s.DeviceId == deviceId
                && (s.Status == ShiftStatus.Open || s.Status == ShiftStatus.Suspended))
            .OrderByDescending(s => s.OpenedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Result<CashierShiftId>> UpdateAsync(CashierShift shift, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shift);

        // The shift was read untracked; attaching it dirty stages the whole row
        // for update, which is all the aggregate owns.
        context.CashierShifts.Update(shift);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<CashierShiftId>.Success(shift.Id);
    }

    /// <inheritdoc />
    public async Task<ShiftLocationFacts?> GetLocationFactsAsync(LocationId locationId, CancellationToken cancellationToken)
    {
        Location? location = await context.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == locationId, cancellationToken)
            .ConfigureAwait(false);

        return location is null
            ? null
            : new ShiftLocationFacts(location.Kind, location.Settings ?? LocationSettings.Default);
    }

    /// <inheritdoc />
    public async Task<ShiftDeviceFacts?> GetDeviceFactsAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        string? shortCode = await context.Devices
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.ShortCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return shortCode is null
            ? null
            : new ShiftDeviceFacts(shortCode);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cash sales are the cash payments against sales posted inside the shift,
    /// joined through the sale's payment shadow key. Cash refunds are the cash
    /// refunds issued inside the shift. Payouts are zero until Batch C adds
    /// petty-cash documents.
    /// </remarks>
    public async Task<ShiftCashTotals> GetShiftCashTotalsAsync(CashierShiftId shiftId, CancellationToken cancellationToken)
    {
        decimal cashSales = await (
            from s in context.Sales.AsNoTracking()
            join p in context.Payments.AsNoTracking()
                on s.Id equals EF.Property<SaleId>(p, "SaleId")
            where s.CashierShiftId == shiftId && p.Method == PaymentMethod.Cash
            select p.Amount)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false);

        decimal cashRefunds = await context.Refunds
            .AsNoTracking()
            .Where(f => f.CashierShiftId == shiftId && f.Method == PaymentMethod.Cash)
            .Select(f => f.Amount)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ShiftCashTotals(cashSales, CashRefunds: cashRefunds, Payouts: 0m);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShiftForceCloseCandidate>> GetForceCloseCandidatesAsync(CancellationToken cancellationToken)
    {
        List<ShiftForceCloseCandidate> candidates = await (
            from s in context.CashierShifts.AsNoTracking()
            join l in context.Locations.AsNoTracking()
                on s.LocationId equals l.Id
            where s.Status == ShiftStatus.Open || s.Status == ShiftStatus.Suspended
            select new ShiftForceCloseCandidate(s, l.Settings.MaxShiftHours))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<PaymentMethod, decimal>> GetRefundedAmountsByMethodAsync(
        SaleId saleId,
        CancellationToken cancellationToken,
        SalesReturnId? excludedReturnId = null)
    {
        Dictionary<PaymentMethod, decimal> refunded = await (
            from r in context.Refunds.AsNoTracking()
            join sr in context.SalesReturns.AsNoTracking()
                on r.SalesReturnId equals sr.Id
            where sr.SaleId == saleId && (excludedReturnId == null || sr.Id != excludedReturnId)
            group r by r.Method into g
            select new
            {
                Method = g.Key,
                Total = g.Sum(f => f.Amount),
            })
            .ToDictionaryAsync(x => x.Method, x => x.Total, cancellationToken)
            .ConfigureAwait(false);

        return refunded;
    }
}
