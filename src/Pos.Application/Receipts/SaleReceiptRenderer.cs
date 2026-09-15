using System.Globalization;
using Pos.Domain.Sales;

namespace Pos.Application.Receipts;

/// <summary>
/// Renders a completed sale as printable plain text — the receipt the customer
/// takes away and the record the device prints at the till. An endpoint seam by
/// design (POS.md §3): the plain-text form is the interim output until the
/// rendering service is replaced by anything richer (thermal layout, tile-for-tile
/// emulation, PDF).
/// </summary>
public static class SaleReceiptRenderer
{
    /// <summary>
    /// Renders a sale receipt as printable plain text, in the same wire form as the
    /// payment-receipt renderer (LF-separated lines, USD-peso decimal presentation).
    /// </summary>
    /// <param name="sale">The completed sale.</param>
    /// <param name="locationName">The display name of the sale's location.</param>
    /// <param name="timeZoneId">The location's IANA time zone, so the completion time
    /// prints as the branch's wall-clock time; UTC is printed when it is unknown.</param>
    /// <param name="issuedByName">The display name of the cashier, when known.</param>
    /// <param name="lineSeparator">The line separator to use (defaults to LF, the wire form).</param>
    /// <returns>The rendering.</returns>
    public static string RenderPlainText(
        Sale sale,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        string lineSeparator = "\n")
    {
        ArgumentNullException.ThrowIfNull(sale);

        string line = lineSeparator;
        string number = sale.Number;

        List<string> lines = [];

        lines.Add("SALE RECEIPT");
        lines.Add(string.Concat(Enumerable.Repeat("=", Math.Max(8, number.Length))));
        lines.Add(number);
        lines.Add($"Sold: {FormatCompleted(sale.CompletedAtUtc, timeZoneId)}");
        lines.Add($"Location: {locationName}");
        lines.Add($"Cashier: {issuedByName ?? string.Empty}");
        lines.Add(string.Empty);

        foreach (SaleItem item in sale.Items)
        {
            lines.AddRange(RenderItem(item));
        }

        lines.Add(string.Empty);
        lines.AddRange(RenderTotals(sale));
        lines.AddRange(RenderPayments(sale));
        lines.Add(string.Empty);
        lines.Add("Thank you.");

        return string.Join(line, lines) + line;
    }

    private static IEnumerable<string> RenderItem(SaleItem item)
    {
        string line = Number(item.LineNumber);
        string name = item.ProductName;
        string quantity = item.Quantity.ToString("N3", CultureInfo.InvariantCulture);
        string unit = item.UnitPrice.ToString("N2", CultureInfo.InvariantCulture);
        string net = item.NetAmount.ToString("N2", CultureInfo.InvariantCulture);

        yield return line + " " + name;
        yield return $"      {quantity} @ {unit}   {net}";

        if (!string.IsNullOrWhiteSpace(item.Barcode))
        {
            yield return $"      {item.Barcode}";
        }

        if (item.Discount > 0m)
        {
            yield return $"      discount {item.Discount.ToString("N2", CultureInfo.InvariantCulture)}";
        }
    }

    private static IEnumerable<string> RenderTotals(Sale sale)
    {
        string exempt = sale.VatExemptTotal.ToString("N2", CultureInfo.InvariantCulture);
        string zero = sale.ZeroRatedTotal.ToString("N2", CultureInfo.InvariantCulture);
        string discount = sale.DiscountTotal.ToString("N2", CultureInfo.InvariantCulture);
        string vat = sale.VatTotal.ToString("N2", CultureInfo.InvariantCulture);
        string net = sale.NetTotal.ToString("N2", CultureInfo.InvariantCulture);

        yield return $"VAT-exempt: {exempt}";
        yield return $"Zero-rated: {zero}";
        yield return $"Discount: {discount}";
        yield return $"VAT ({VatRateLabel(sale)}): {vat}";
        yield return $"TOTAL: {net}";
    }

    private static IEnumerable<string> RenderPayments(Sale sale)
    {
        foreach (Payment payment in sale.Payments)
        {
            string method = PaymentMethodLabel(payment.Method);
            string amount = payment.Amount.ToString("N2", CultureInfo.InvariantCulture);
            yield return $"Paid by {method}: {amount}";

            if (payment.Tendered is { } tendered)
            {
                string tenderedText = tendered.ToString("N2", CultureInfo.InvariantCulture);
                string change = (payment.Change ?? 0m).ToString("N2", CultureInfo.InvariantCulture);
                yield return $"  Tendered: {tenderedText}";
                yield return $"  Change: {change}";
            }

            if (!string.IsNullOrWhiteSpace(payment.ProviderReference))
            {
                yield return $"  Reference: {payment.ProviderReference}";
            }
        }
    }

    private static string FormatCompleted(DateTimeOffset completedAtUtc, string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out TimeZoneInfo? zone))
        {
            DateTimeOffset local = TimeZoneInfo.ConvertTime(completedAtUtc, zone);
            return string.Concat(
                local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), " (", timeZoneId, ")");
        }

        return completedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    private static string VatRateLabel(Sale sale)
    {
        decimal? rate = sale.Items
            .Select(i => i.VatRate)
            .FirstOrDefault(v => v is not null);

        return rate is { } percent
            ? (percent * 100m).ToString("N1", CultureInfo.InvariantCulture) + "%"
            : "0%";
    }

    private static string PaymentMethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.Card => "Card",
        PaymentMethod.EWallet => "EWallet",
        _ => method.ToString(),
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture) + ".";
}