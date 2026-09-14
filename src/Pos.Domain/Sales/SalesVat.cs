using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// Philippine VAT arithmetic for sale lines (POS.md §2.6). Sale prices are
/// tax-inclusive: the charge already contains the VAT, so the base is derived by
/// dividing and the VAT is whatever remains. Each leg is rounded independently
/// at <see cref="Money.StorageScale"/> with the same midpoint policy the Money
/// type uses for intermediate values, so the stored numbers reconcile exactly:
/// <c>VatBase + Vat == Gross</c>.
/// </summary>
public static class SalesVat
{
    /// <summary>
    /// Splits a tax-inclusive charge into its VAT base and VAT.
    /// </summary>
    /// <param name="grossAmount">The tax-inclusive charge.</param>
    /// <param name="vatRate">The VAT rate as a fraction, for example <c>0.12m</c>.</param>
    /// <returns>The base and the VAT, each rounded to <see cref="Money.StorageScale"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The charge is negative or the rate is not positive.</exception>
    public static (decimal VatBase, decimal Vat) SplitTaxInclusive(decimal grossAmount, decimal vatRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(grossAmount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vatRate);

        decimal vatBase = decimal.Round(grossAmount / (1m + vatRate), Money.StorageScale, Money.IntermediateRounding);
        decimal vat = decimal.Round(grossAmount - vatBase, Money.StorageScale, Money.IntermediateRounding);
        return (vatBase, vat);
    }
}