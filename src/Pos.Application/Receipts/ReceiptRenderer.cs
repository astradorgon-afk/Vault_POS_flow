using System.Globalization;
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
}