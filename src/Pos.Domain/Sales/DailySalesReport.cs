using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// Aggregated sales activity at a single location for one business date: totals,
/// payments by method, per-shift breakdowns, and refund amounts.
/// </summary>
public sealed record DailySalesReport(
    LocationId LocationId,
    string LocationName,
    DateOnly BusinessDate,
    DailySalesReportSalesSummary SalesSummary,
    IReadOnlyList<DailySalesReportPaymentMethodSummary> PaymentsByMethod,
    IReadOnlyList<DailySalesReportShiftSummary> Shifts);

/// <summary>Sales and refund totals for the day.</summary>
public sealed record DailySalesReportSalesSummary(
    int SalesCount,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal NetTotal,
    decimal VatTotal,
    decimal VatExemptTotal,
    decimal ZeroRatedTotal,
    decimal TaxableBaseTotal,
    decimal RefundTotal);

/// <summary>Amount collected through one payment method.</summary>
public sealed record DailySalesReportPaymentMethodSummary(
    string Method,
    decimal Amount,
    decimal ChangeGiven);

/// <summary>A single cashier shift's contribution to the day.</summary>
public sealed record DailySalesReportShiftSummary(
    Guid ShiftId,
    string ShiftNumber,
    Guid CashierUserId,
    string Status,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    decimal OpeningFloat,
    int SalesCount,
    decimal NetTotal,
    decimal CashSalesTotal,
    decimal CashRefundsTotal);
