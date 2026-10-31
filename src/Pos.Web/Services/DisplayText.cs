using System.Text.RegularExpressions;

namespace Pos.Web.Services;

/// <summary>Tidies text the server stored for the record before a person reads it.</summary>
public static partial class DisplayText
{
    /// <summary>
    /// Removes the ledger's fixed four decimals from quantities in a sentence, so
    /// "16.0000 units available" reads "16 units available" and "12.5000" reads
    /// "12.5". The stored text is unchanged.
    /// </summary>
    /// <param name="text">The stored text.</param>
    /// <returns>The text with whole and trailing-zero decimals shortened.</returns>
    public static string TidyQuantities(string text)
        => string.IsNullOrEmpty(text)
            ? text
            : LedgerDecimal().Replace(text, match =>
            {
                string fraction = match.Groups["fraction"].Value.TrimEnd('0');
                return fraction.Length == 0
                    ? match.Groups["whole"].Value
                    : $"{match.Groups["whole"].Value}.{fraction}";
            });

    [GeneratedRegex(@"(?<![\d.])(?<whole>\d[\d,]*)\.(?<fraction>\d{4})(?!\d|\.\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LedgerDecimal();
}
