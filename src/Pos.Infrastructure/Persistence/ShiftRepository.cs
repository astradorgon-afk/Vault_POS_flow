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
    /// joined through the sale's payment shadow key. Refunds and payouts are
    /// zero until Batch C adds those documents to the shift total.
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

        return new ShiftCashTotals(cashSales, CashRefunds: 0m, Payouts: 0m);
    }
}