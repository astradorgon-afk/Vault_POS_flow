using System.Globalization;
using System.Net;

namespace Pos.Application.Receipts;

/// <summary>
/// Shared plumbing for the HTML receipt renderers: the self-contained document
/// shell, the print stylesheet, and the escaping every user-supplied value must
/// pass through before it reaches the markup.
/// </summary>
internal static class ReceiptHtml
{
    /// <summary>Escapes text for safe placement inside HTML.</summary>
    internal static string Escape(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>Formats money the same way the text renderers do.</summary>
    internal static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Wraps a body in a complete, printable HTML document.</summary>
    internal static string Document(string title, string body)
        => string.Concat(
            "<!DOCTYPE html>", "\n",
            "<html lang=\"en\">", "\n",
            "<head>", "\n",
            "<meta charset=\"utf-8\" />", "\n",
            "<title>", Escape(title), "</title>", "\n",
            "<style>", Style, "</style>", "\n",
            "</head>", "\n",
            "<body>", "\n",
            body, "\n",
            "</body>", "\n",
            "</html>");

    private const string Style =
        ":root { color-scheme: light; }" +
        "* { box-sizing: border-box; }" +
        "html, body { margin: 0; padding: 0; }" +
        "@page { size: 80mm auto; margin: 4mm; }" +
        "body { font-family: 'Courier New', Courier, monospace; font-size: 12px; line-height: 1.4; color: #111; background: #fff; }" +
        ".receipt { width: 72mm; margin: 0 auto; }" +
        ".receipt-head { text-align: center; margin-bottom: 3mm; }" +
        ".receipt-head .brand:first-child { font-weight: bold; font-size: 13px; }" +
        ".receipt-head h1 { font-size: 16px; letter-spacing: 0.12em; text-transform: uppercase; margin: 0 0 1mm; }" +
        ".receipt-head .number { font-weight: bold; margin-bottom: 1mm; }" +
        ".meta span { display: block; white-space: nowrap; }" +
        ".seal { border: 0; border-top: 1px dashed #333; margin: 2.5mm 0; }" +
        "table { width: 100%; border-collapse: collapse; }" +
        "td { vertical-align: top; }" +
        "td.amount { text-align: right; white-space: nowrap; padding-left: 2mm; }" +
        "tr.qty td { font-size: 11px; color: #333; }" +
        "tr.grand td { font-weight: bold; border-top: 1px solid #111; padding-top: 1mm; }" +
        "td.indent { padding-left: 4mm; }" +
        "footer { text-align: center; margin-top: 3mm; }";
}