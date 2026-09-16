namespace Pos.Application.Receipts;

/// <summary>
/// The rendering a caller asks a receipt endpoint to produce. The plain-text
/// form stays the default so existing terminals keep printing unchanged; the
/// thermal-column and HTML layouts are the richer forms the receipt seam
/// (ADR-0026, POS.md §3) exists for.
/// </summary>
public enum ReceiptFormat
{
    /// <summary>The classic LF-separated plain-text form.</summary>
    Plain = 0,

    /// <summary>A fixed 42-column, 80 mm thermal-column layout.</summary>
    Thermal = 1,

    /// <summary>A self-contained HTML document designed for browser print-to-PDF.</summary>
    Html = 2,
}
