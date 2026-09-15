using Microsoft.EntityFrameworkCore;
using Pos.Application.Sales;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Infrastructure.Persistence;

/// <inheritdoc cref="IDailySalesReportRepository" />
/// <remarks>
/// The report is a pure read: every query is <c>AsNoTracking</c> and follows the
/// same shadow-key payment join the shift totals use, so completed-sale payment
/// sums can never drift apart from what a shift's cash reconciliation sees.
/// </remarks>
public sealed class DailySalesReportRepository(PosDbContext context) : IDailySalesReportRepository
{
    /// <inheritdoc />
    public async Task<DailySalesReport?> GetDailySalesReportAsync(
        LocationId locationId,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        string? locationName = await context.Locations
            .AsNoTracking()
            .Where(l => l.Id == locationId)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (locationName is null)
        {
            return null;
        }

        DailySalesReportSalesSummary core = await (
            from s in context.Sales.AsNoTracking()
            where s.LocationId == locationId && s.BusinessDate == businessDate && s.Status == SaleStatus.Completed
            group s by 1 into g
            select new DailySalesReportSalesSummary(
                g.Count(),
                g.Sum(x => x.GrossTotal),
                g.Sum(x => x.DiscountTotal),
                g.Sum(x => x.NetTotal),
                g.Sum(x => x.VatTotal),
                g.Sum(x => x.VatExemptTotal),
                g.Sum(x => x.ZeroRatedTotal),
                g.Sum(x => x.TaxableBaseTotal),
                0m))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? new DailySalesReportSalesSummary(0, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

        decimal refundTotal = await (
            from r in context.Refunds.AsNoTracking()
            join sh in context.CashierShifts.AsNoTracking()
                on r.CashierShiftId equals sh.Id
            where sh.LocationId == locationId && sh.BusinessDate == businessDate
            select r.Amount)
            .SumAsync(cancellationToken)
            .ConfigureAwait(false);

        List<DailySalesReportPaymentMethodSummary> payments = await (
            from s in context.Sales.AsNoTracking()
            where s.LocationId == locationId && s.BusinessDate == businessDate && s.Status == SaleStatus.Completed
            join p in context.Payments.AsNoTracking()
                on s.Id equals EF.Property<SaleId>(p, "SaleId")
            group p by p.Method into g
            select new DailySalesReportPaymentMethodSummary(
                g.Key.ToString(),
                g.Sum(x => x.Amount),
                g.Sum(x => x.Change) ?? 0m))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ShiftSalesStats> shiftStats = await (
            from s in context.Sales.AsNoTracking()
            where s.LocationId == locationId && s.BusinessDate == businessDate && s.Status == SaleStatus.Completed
            group s by s.CashierShiftId into g
            select new ShiftSalesStats(
                g.Key,
                g.Count(),
                g.Sum(x => x.NetTotal)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ShiftCashStats> shiftCash = await (
            from s in context.Sales.AsNoTracking()
            where s.LocationId == locationId && s.BusinessDate == businessDate && s.Status == SaleStatus.Completed
            join p in context.Payments.AsNoTracking()
                on s.Id equals EF.Property<SaleId>(p, "SaleId")
            where p.Method == PaymentMethod.Cash
            group p by s.CashierShiftId into g
            select new ShiftCashStats(g.Key, g.Sum(x => x.Amount)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ShiftCashStats> shiftRefunds = await (
            from r in context.Refunds.AsNoTracking()
            join sh in context.CashierShifts.AsNoTracking()
                on r.CashierShiftId equals sh.Id
            where sh.LocationId == locationId && sh.BusinessDate == businessDate && r.Method == PaymentMethod.Cash
            group r by sh.Id into g
            select new ShiftCashStats(g.Key, g.Sum(x => x.Amount)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<CashierShift> shifts = await context.CashierShifts
            .AsNoTracking()
            .Where(sh => sh.LocationId == locationId && sh.BusinessDate == businessDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<CashierShiftId, ShiftSalesStats> statsByShift = shiftStats.ToDictionary(x => x.ShiftId);
        Dictionary<CashierShiftId, decimal> cashByShift = shiftCash.ToDictionary(x => x.ShiftId, x => x.Amount);
        Dictionary<CashierShiftId, decimal> refundsByShift = shiftRefunds.ToDictionary(x => x.ShiftId, x => x.Amount);

        List<DailySalesReportShiftSummary> shiftRows = [];
        foreach (CashierShift sh in shifts)
        {
            ShiftSalesStats? stats = statsByShift.TryGetValue(sh.Id, out ShiftSalesStats? found) ? found : null;
            cashByShift.TryGetValue(sh.Id, out decimal cash);
            refundsByShift.TryGetValue(sh.Id, out decimal refunded);

            shiftRows.Add(new DailySalesReportShiftSummary(
                sh.Id.Value,
                sh.Number,
                sh.CashierUserId.Value,
                sh.Status.ToString(),
                sh.OpenedAtUtc,
                sh.ClosedAtUtc,
                sh.OpeningFloat,
                stats?.SalesCount ?? 0,
                stats?.NetTotal ?? 0m,
                cash,
                refunded));
        }

        return new DailySalesReport(
            locationId,
            locationName,
            businessDate,
            core with { RefundTotal = refundTotal },
            payments,
            shiftRows);
    }

    private sealed record ShiftSalesStats(CashierShiftId ShiftId, int SalesCount, decimal NetTotal);

    private sealed record ShiftCashStats(CashierShiftId ShiftId, decimal Amount);
}