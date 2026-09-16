namespace Pos.Application.Receipts;

/// <summary>
/// Column helpers shared by the thermal renderers. An 80 mm thermal head
/// prints 42 characters per line at normal (font A) pitch, so every emitted
/// line is padded to exactly <see cref="Width"/> columns with the amount
/// column fixed at the right edge — the layout a till receipt is cut against.
/// </summary>
internal static class ReceiptThermal
{
    /// <summary>Character width of an 80 mm thermal line (Epson 42-column font A).</summary>
    internal const int Width = 42;

    /// <summary>Width of the right-aligned amount column.</summary>
    internal const int AmountWidth = 12;

    /// <summary>The width of the descriptive (label) column.</summary>
    internal const int LabelWidth = Width - AmountWidth;

    /// <summary>A full-width dashed separator.</summary>
    internal static string Divider => new('-', Width);

    /// <summary>Truncates a string to a width, signalling the cut with an ellipsis.</summary>
    internal static string Fit(string text, int width)
        => text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";

    /// <summary>Centers a line across the full thermal width.</summary>
    internal static string Center(string text)
    {
        if (text.Length >= Width)
        {
            return Fit(text, Width);
        }

        int left = (Width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', Width - text.Length - left);
    }

    /// <summary>Builds a label/amount line with the amount flush right.</summary>
    internal static string Row(string label, string amount)
        => Fit(label, LabelWidth).PadRight(LabelWidth) + Fit(amount, AmountWidth).PadLeft(AmountWidth);

    /// <summary>Pads any remaining line to the full width.</summary>
    internal static string Line(string? text = null)
        => Fit(text ?? string.Empty, Width).PadRight(Width);
}
