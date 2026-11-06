using System.Globalization;
using System.Text;
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
    /// <param name="header">The branch's receipt header (business name, TIN), printed first; one line per line break.</param>
    /// <param name="footer">The branch's receipt footer (return policy, thanks), printed last in place of the default.</param>
    /// <returns>The rendering.</returns>
    public static string RenderPlainText(
        Sale sale,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        string lineSeparator = "\n",
        string? header = null,
        string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        string line = lineSeparator;
        string number = sale.Number;

        List<string> lines = [];

        if (TextLines(header) is { Count: > 0 } headerLines)
        {
            lines.AddRange(headerLines);
            lines.Add(string.Empty);
        }

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
        lines.AddRange(TextLines(footer) is { Count: > 0 } footerLines ? footerLines : ["Thank you."]);

        return string.Join(line, lines) + line;
    }

    /// <summary>
    /// Renders a sale receipt in the requested <see cref="ReceiptFormat"/>,
    /// keeping the plain-text form as the default.
    /// </summary>
    public static string Render(
        Sale sale,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        ReceiptFormat format,
        string lineSeparator = "\n",
        string? header = null,
        string? footer = null)
        => format switch
        {
            ReceiptFormat.Thermal => RenderThermal(sale, locationName, timeZoneId, issuedByName, lineSeparator, header, footer),
            ReceiptFormat.Html => RenderHtml(sale, locationName, timeZoneId, issuedByName, header, footer),
            _ => RenderPlainText(sale, locationName, timeZoneId, issuedByName, lineSeparator, header, footer),
        };

    /// <summary>
    /// Renders a sale receipt as a fixed 42-column, 80 mm thermal layout:
    /// every line is cut-width and amounts align at the right edge, ready for a
    /// device thermal head.
    /// </summary>
    public static string RenderThermal(
        Sale sale,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        string lineSeparator = "\n",
        string? header = null,
        string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        string number = sale.Number;

        List<string> lines = [];
        if (TextLines(header) is { Count: > 0 } headerLines)
        {
            lines.AddRange(headerLines.SelectMany(WrapThermal).Select(ReceiptThermal.Center));
            lines.Add(ReceiptThermal.Line());
        }

        lines.AddRange(
        [
            ReceiptThermal.Center("SALE RECEIPT"),
            ReceiptThermal.Center(new string('=', Math.Max(8, number.Length))),
            ReceiptThermal.Center(number),
            ReceiptThermal.Line($"Sold: {FormatCompleted(sale.CompletedAtUtc, timeZoneId)}"),
            ReceiptThermal.Line($"Location: {locationName}"),
            ReceiptThermal.Line($"Cashier: {issuedByName ?? string.Empty}"),
            ReceiptThermal.Divider,
        ]);

        foreach (SaleItem item in sale.Items)
        {
            lines.AddRange(RenderThermalItem(item));
        }

        lines.Add(ReceiptThermal.Divider);
        lines.AddRange(RenderThermalTotals(sale));
        lines.AddRange(RenderThermalPayments(sale));
        lines.Add(ReceiptThermal.Divider);
        lines.AddRange(TextLines(footer) is { Count: > 0 } footerLines
            ? footerLines.SelectMany(WrapThermal).Select(ReceiptThermal.Center)
            : [ReceiptThermal.Center("Thank you.")]);

        return string.Join(lineSeparator, lines) + lineSeparator;
    }

    /// <summary>
    /// Renders a sale receipt as a self-contained HTML document sized for an
    /// 80 mm printout — the browser's print-to-PDF path for a cashier who
    /// needs a readable, copyable copy. Every value is HTML-escaped.
    /// </summary>
    public static string RenderHtml(
        Sale sale,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        string? header = null,
        string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        StringBuilder body = new();

        body.Append("<div class=\"receipt\">").Append('\n');
        body.Append("  <header class=\"receipt-head\">").Append('\n');
        foreach (string headerLine in TextLines(header))
        {
            body.Append("    <div class=\"brand\">").Append(ReceiptHtml.Escape(headerLine)).Append("</div>").Append('\n');
        }

        body.Append("    <h1>Sale Receipt</h1>").Append('\n');
        body.Append("    <div class=\"number\">").Append(ReceiptHtml.Escape(sale.Number)).Append("</div>").Append('\n');
        body.Append("    <div class=\"meta\">").Append('\n');
        body.Append("      <span>Sold: ").Append(ReceiptHtml.Escape(FormatCompleted(sale.CompletedAtUtc, timeZoneId))).Append("</span>").Append('\n');
        body.Append("      <span>Location: ").Append(ReceiptHtml.Escape(locationName)).Append("</span>").Append('\n');
        body.Append("      <span>Cashier: ").Append(ReceiptHtml.Escape(issuedByName ?? string.Empty)).Append("</span>").Append('\n');
        body.Append("    </div>").Append('\n');
        body.Append("  </header>").Append('\n');
        body.Append("  <hr class=\"seal\" />").Append('\n');
        body.Append("  <table class=\"items\">").Append('\n');
        body.Append("    <tbody>").Append('\n');

        foreach (SaleItem item in sale.Items)
        {
            body.Append("      <tr>").Append('\n');
            body.Append("        <td>")
                .Append(ReceiptHtml.Escape($"{Number(item.LineNumber)} {item.ProductName}"))
                .Append("</td>").Append('\n');
            body.Append("        <td class=\"amount\">")
                .Append(ReceiptHtml.Escape(Money(item.NetAmount)))
                .Append("</td>").Append('\n');
            body.Append("      </tr>").Append('\n');

            string qty = $"{item.Quantity.ToString("N3", CultureInfo.InvariantCulture)} @ {Money(item.UnitPrice)}";
            if (!string.IsNullOrWhiteSpace(item.Barcode))
            {
                qty += " · " + item.Barcode;
            }

            if (item.Discount > 0m)
            {
                qty += " · discount " + Money(item.Discount);
            }

            body.Append("      <tr class=\"qty\"><td colspan=\"2\">")
                .Append(ReceiptHtml.Escape(qty))
                .Append("</td></tr>").Append('\n');
        }

        body.Append("    </tbody>").Append('\n');
        body.Append("  </table>").Append('\n');
        body.Append("  <hr class=\"seal\" />").Append('\n');
        body.Append("  <table class=\"totals\">").Append('\n');
        body.Append("    <tbody>").Append('\n');
        body.Append(HtmlRow("VAT-exempt:", Money(sale.VatExemptTotal)));
        body.Append(HtmlRow("Zero-rated:", Money(sale.ZeroRatedTotal)));
        body.Append(HtmlRow("Discount:", Money(sale.DiscountTotal)));
        body.Append(HtmlRow($"VAT ({VatRateLabel(sale)}):", Money(sale.VatTotal)));
        body.Append("      <tr class=\"grand\"><td>TOTAL:</td><td class=\"amount\">")
            .Append(ReceiptHtml.Escape(Money(sale.NetTotal)))
            .Append("</td></tr>").Append('\n');
        body.Append("    </tbody>").Append('\n');
        body.Append("  </table>").Append('\n');
        body.Append("  <hr class=\"seal\" />").Append('\n');
        body.Append("  <table class=\"payments\">").Append('\n');
        body.Append("    <tbody>").Append('\n');

        foreach (Payment payment in sale.Payments)
        {
            string method = PaymentMethodLabel(payment.Method);
            body.Append("      <tr><td>Paid by ").Append(ReceiptHtml.Escape(method))
                .Append(":</td><td class=\"amount\">")
                .Append(ReceiptHtml.Escape(Money(payment.Amount)))
                .Append("</td></tr>").Append('\n');

            if (payment.Tendered is { } tendered)
            {
                body.Append("      <tr><td class=\"indent\">Tendered:</td><td class=\"amount\">")
                    .Append(ReceiptHtml.Escape(Money(tendered)))
                    .Append("</td></tr>").Append('\n');
                body.Append("      <tr><td class=\"indent\">Change:</td><td class=\"amount\">")
                    .Append(ReceiptHtml.Escape(Money(payment.Change ?? 0m)))
                    .Append("</td></tr>").Append('\n');
            }

            if (!string.IsNullOrWhiteSpace(payment.ProviderReference))
            {
                body.Append("      <tr><td class=\"indent\">Reference:</td><td>")
                    .Append(ReceiptHtml.Escape(payment.ProviderReference))
                    .Append("</td></tr>").Append('\n');
            }
        }

        body.Append("    </tbody>").Append('\n');
        body.Append("  </table>").Append('\n');
        List<string> footerLines = TextLines(footer);
        body.Append("  <footer>")
            .Append(footerLines.Count == 0 ? "Thank you." : string.Join("<br />", footerLines.Select(ReceiptHtml.Escape)))
            .Append("</footer>").Append('\n');
        body.Append("</div>").Append('\n');

        return ReceiptHtml.Document($"SALE RECEIPT — {sale.Number}", body.ToString());
    }

    /// <summary>A branch's configured receipt text as trimmed, non-empty lines.</summary>
    private static List<string> TextLines(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)];

    /// <summary>Word-wraps a line to the thermal width instead of cutting it off.</summary>
    private static IEnumerable<string> WrapThermal(string text)
    {
        string current = string.Empty;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > ReceiptThermal.Width)
            {
                yield return current;
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : current + " " + word;
            }
        }

        if (current.Length > 0)
        {
            yield return current;
        }
    }

    private static IEnumerable<string> RenderThermalItem(SaleItem item)
    {
        string quantity = item.Quantity.ToString("N3", CultureInfo.InvariantCulture);
        string unit = item.UnitPrice.ToString("N2", CultureInfo.InvariantCulture);
        string net = item.NetAmount.ToString("N2", CultureInfo.InvariantCulture);

        yield return ReceiptThermal.Line($"{Number(item.LineNumber)} {item.ProductName}");
        yield return ReceiptThermal.Row($"     {quantity} @ {unit}", net);

        if (!string.IsNullOrWhiteSpace(item.Barcode))
        {
            yield return ReceiptThermal.Line($"     {item.Barcode}");
        }

        if (item.Discount > 0m)
        {
            yield return ReceiptThermal.Row(
                "     discount", item.Discount.ToString("N2", CultureInfo.InvariantCulture));
        }
    }

    private static IEnumerable<string> RenderThermalTotals(Sale sale)
    {
        yield return ReceiptThermal.Row("VAT-exempt:", Money(sale.VatExemptTotal));
        yield return ReceiptThermal.Row("Zero-rated:", Money(sale.ZeroRatedTotal));
        yield return ReceiptThermal.Row("Discount:", Money(sale.DiscountTotal));
        yield return ReceiptThermal.Row($"VAT ({VatRateLabel(sale)}):", Money(sale.VatTotal));
        yield return ReceiptThermal.Row("TOTAL:", Money(sale.NetTotal));
    }

    private static IEnumerable<string> RenderThermalPayments(Sale sale)
    {
        foreach (Payment payment in sale.Payments)
        {
            string method = PaymentMethodLabel(payment.Method);
            yield return ReceiptThermal.Row($"Paid by {method}:", Money(payment.Amount));

            if (payment.Tendered is { } tendered)
            {
                yield return ReceiptThermal.Row("  Tendered:", Money(tendered));
                yield return ReceiptThermal.Row("  Change:", Money(payment.Change ?? 0m));
            }

            if (!string.IsNullOrWhiteSpace(payment.ProviderReference))
            {
                yield return ReceiptThermal.Line($"  Reference: {payment.ProviderReference}");
            }
        }
    }

    private static string HtmlRow(string label, string amount)
        => "      <tr><td>" + ReceiptHtml.Escape(label)
           + "</td><td class=\"amount\">" + ReceiptHtml.Escape(amount)
           + "</td></tr>\n";

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

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
