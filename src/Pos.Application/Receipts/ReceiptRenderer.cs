using System.Globalization;
using System.Text;
using Pos.Domain.Receipts;

namespace Pos.Application.Receipts;

/// <summary>
/// Renders a payment receipt for printing or email. An endpoint seam by design
/// (ADR-0026): the plain-text form is the interim output until the rendering
/// service is replaced by anything richer (thermal layout, PDF).
/// </summary>
public static class ReceiptRenderer
{
    /// <summary>Renders a receipt as printable plain text.</summary>
    /// <param name="receipt">The receipt.</param>
    /// <param name="locationName">The display name of the receipt's location.</param>
    /// <param name="timeZoneId">The location's IANA time zone, so the issue time
    /// prints as the branch's wall-clock time; UTC is printed when it is unknown.</param>
    /// <param name="issuedByName">The display name of the issuing user, when known.</param>
    /// <param name="lineSeparator">The line separator to use (defaults to LF, the wire form).</param>
    /// <returns>The rendering.</returns>
    public static string RenderPlainText(
        Receipt receipt,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        string lineSeparator = "\n")
    {
        ArgumentNullException.ThrowIfNull(receipt);

        string line = lineSeparator;
        string money = receipt.Amount.ToString("N2", CultureInfo.InvariantCulture);

        return string.Concat(
            "PAYMENT RECEIPT", line,
            "===============", line,
            $"{receipt.Number}", line,
            $"Issued: {FormatIssued(receipt.IssuedAtUtc, timeZoneId)}", line,
            $"Location: {locationName}", line,
            $"Type: {KindLabel(receipt.Kind)}", line,
            $"Amount: {money}", line,
            AppendOption(receipt.Counterparty, $"Counterparty: {receipt.Counterparty}", line),
            AppendOption(receipt.ReferenceNumber, $"Reference: {receipt.ReferenceNumber}", line),
            AppendOption(receipt.Note, $"Note: {receipt.Note}", line),
            string.IsNullOrWhiteSpace(issuedByName) ? string.Empty : $"Issued by: {issuedByName}{line}",
            line);
    }

    private static string FormatIssued(DateTimeOffset issuedAtUtc, string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out TimeZoneInfo? zone))
        {
            DateTimeOffset local = TimeZoneInfo.ConvertTime(issuedAtUtc, zone);
            return string.Concat(
                local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), " (", timeZoneId, ")");
        }

        return issuedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    private static string KindLabel(ReceiptKind kind) => kind switch
    {
        ReceiptKind.WalkInSale => "Walk-in sale",
        ReceiptKind.BranchExpense => "Branch expense",
        ReceiptKind.OwnerWithdrawal => "Owner withdrawal",
        _ => kind.ToString(),
    };

    private static string AppendOption(string? value, string rendered, string line)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : rendered + line;

    /// <summary>
    /// Renders a payment receipt in the requested <see cref="ReceiptFormat"/>,
    /// keeping the plain-text form as the default.
    /// </summary>
    public static string Render(
        Receipt receipt,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        ReceiptFormat format,
        string lineSeparator = "\n")
        => format switch
        {
            ReceiptFormat.Thermal => RenderThermal(receipt, locationName, timeZoneId, issuedByName, lineSeparator),
            ReceiptFormat.Html => RenderHtml(receipt, locationName, timeZoneId, issuedByName),
            _ => RenderPlainText(receipt, locationName, timeZoneId, issuedByName, lineSeparator),
        };

    /// <summary>
    /// Renders a payment receipt as a fixed 42-column, 80 mm thermal layout.
    /// </summary>
    public static string RenderThermal(
        Receipt receipt,
        string locationName,
        string? timeZoneId,
        string? issuedByName,
        string lineSeparator = "\n")
    {
        ArgumentNullException.ThrowIfNull(receipt);

        string number = receipt.Number;
        string money = receipt.Amount.ToString("N2", CultureInfo.InvariantCulture);

        List<string> lines =
        [
            ReceiptThermal.Center("PAYMENT RECEIPT"),
            ReceiptThermal.Center(new string('=', Math.Max(8, number.Length))),
            ReceiptThermal.Center(number),
            ReceiptThermal.Line($"Issued: {FormatIssued(receipt.IssuedAtUtc, timeZoneId)}"),
            ReceiptThermal.Line($"Location: {locationName}"),
            ReceiptThermal.Line($"Type: {KindLabel(receipt.Kind)}"),
            ReceiptThermal.Row("Amount:", money),
        ];

        if (!string.IsNullOrWhiteSpace(receipt.Counterparty))
        {
            lines.Add(ReceiptThermal.Line($"Counterparty: {receipt.Counterparty}"));
        }

        if (!string.IsNullOrWhiteSpace(receipt.ReferenceNumber))
        {
            lines.Add(ReceiptThermal.Line($"Reference: {receipt.ReferenceNumber}"));
        }

        if (!string.IsNullOrWhiteSpace(receipt.Note))
        {
            lines.Add(ReceiptThermal.Line($"Note: {receipt.Note}"));
        }

        if (!string.IsNullOrWhiteSpace(issuedByName))
        {
            lines.Add(ReceiptThermal.Line($"Issued by: {issuedByName}"));
        }

        lines.Add(ReceiptThermal.Center("Thank you."));

        return string.Join(lineSeparator, lines) + lineSeparator;
    }

    /// <summary>
    /// Renders a payment receipt as a self-contained HTML document sized for an
    /// 80 mm printout — the browser's print-to-PDF path. Every value is
    /// HTML-escaped.
    /// </summary>
    public static string RenderHtml(
        Receipt receipt,
        string locationName,
        string? timeZoneId,
        string? issuedByName)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        StringBuilder body = new();

        body.Append("<div class=\"receipt\">").Append('\n');
        body.Append("  <header class=\"receipt-head\">").Append('\n');
        body.Append("    <h1>Payment Receipt</h1>").Append('\n');
        body.Append("    <div class=\"number\">").Append(ReceiptHtml.Escape(receipt.Number)).Append("</div>").Append('\n');
        body.Append("    <div class=\"meta\">").Append('\n');
        body.Append("      <span>Issued: ").Append(ReceiptHtml.Escape(FormatIssued(receipt.IssuedAtUtc, timeZoneId))).Append("</span>").Append('\n');
        body.Append("      <span>Location: ").Append(ReceiptHtml.Escape(locationName)).Append("</span>").Append('\n');
        body.Append("    </div>").Append('\n');
        body.Append("  </header>").Append('\n');
        body.Append("  <hr class=\"seal\" />").Append('\n');
        body.Append("  <table class=\"details\">").Append('\n');
        body.Append("    <tbody>").Append('\n');
        body.Append(HtmlRow("Type:", KindLabel(receipt.Kind)));
        body.Append(HtmlRow("Amount:", receipt.Amount.ToString("N2", CultureInfo.InvariantCulture)));

        if (!string.IsNullOrWhiteSpace(receipt.Counterparty))
        {
            body.Append(HtmlRow("Counterparty:", receipt.Counterparty));
        }

        if (!string.IsNullOrWhiteSpace(receipt.ReferenceNumber))
        {
            body.Append(HtmlRow("Reference:", receipt.ReferenceNumber));
        }

        if (!string.IsNullOrWhiteSpace(receipt.Note))
        {
            body.Append(HtmlRow("Note:", receipt.Note));
        }

        if (!string.IsNullOrWhiteSpace(issuedByName))
        {
            body.Append(HtmlRow("Issued by:", issuedByName));
        }

        body.Append("    </tbody>").Append('\n');
        body.Append("  </table>").Append('\n');
        body.Append("  <footer>Thank you.</footer>").Append('\n');
        body.Append("</div>").Append('\n');

        return ReceiptHtml.Document($"PAYMENT RECEIPT — {receipt.Number}", body.ToString());
    }

    private static string HtmlRow(string label, string value)
        => "      <tr><td>" + ReceiptHtml.Escape(label)
           + "</td><td>" + ReceiptHtml.Escape(value)
           + "</td></tr>\n";
}
