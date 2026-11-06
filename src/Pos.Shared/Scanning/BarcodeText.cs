using System.Text;

namespace Pos.Shared.Scanning;

/// <summary>Cleans scanned text and knows the equivalent forms of retail codes.</summary>
public static class BarcodeText
{
    /// <summary>The GS1 group separator (FNC1) that some codes carry between fields.</summary>
    public const char GroupSeparator = '\u001d';

    /// <summary>The longest text accepted as one barcode.</summary>
    public const int MaximumLength = 128;

    /// <summary>
    /// Removes what scanners add around a code: line endings and other control
    /// characters, surrounding spaces, and an AIM symbology prefix such as
    /// <c>]E0</c> when the scanner is set to send one.
    /// </summary>
    /// <param name="raw">What the scanner sent.</param>
    /// <returns>The code, or an empty string when nothing usable remains.</returns>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        StringBuilder kept = new(raw.Length);
        foreach (char c in raw)
        {
            if (!char.IsControl(c) || c == GroupSeparator)
            {
                kept.Append(c);
            }
        }

        string code = kept.ToString().Trim().Trim(GroupSeparator);
        if (code.Length >= 3 && code[0] == ']' && char.IsLetter(code[1]) && char.IsLetterOrDigit(code[2]))
        {
            code = code[3..].TrimStart();
        }

        return code.Length > MaximumLength ? code[..MaximumLength] : code;
    }

    /// <summary>
    /// Gets the forms a product might be stored under for this code, most
    /// likely first. A UPC-A scan (12 digits) matches the same product saved as
    /// EAN-13 with a leading zero, and the other way round.
    /// </summary>
    /// <param name="code">A cleaned code.</param>
    /// <returns>The code and its equivalents.</returns>
    public static IReadOnlyList<string> LookupVariants(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return [];
        }

        List<string> variants = [code];
        if (IsAllDigits(code))
        {
            if (code.Length == 12)
            {
                variants.Add("0" + code);
            }
            else if (code.Length == 13 && code[0] == '0')
            {
                variants.Add(code[1..]);
            }
            else if (code.Length == 14 && code[0] == '0')
            {
                variants.Add(code[1..]);
            }
        }

        return variants;
    }

    /// <summary>
    /// Gets whether a GTIN (EAN-8, UPC-A, EAN-13 or GTIN-14) has a correct check
    /// digit. A wrong one usually means a misread or a hand-typed mistake.
    /// </summary>
    /// <param name="code">The code.</param>
    /// <returns>True when it is a GTIN with a valid check digit.</returns>
    public static bool IsValidGtin(string? code)
    {
        if (code is null || code.Length is not (8 or 12 or 13 or 14) || !IsAllDigits(code))
        {
            return false;
        }

        // From the right, excluding the check digit, weights alternate 3, 1, 3…
        int sum = 0;
        for (int i = code.Length - 2, weight = 3; i >= 0; i--, weight = weight == 3 ? 1 : 3)
        {
            sum += (code[i] - '0') * weight;
        }

        int check = (10 - (sum % 10)) % 10;
        return check == code[^1] - '0';
    }

    private static bool IsAllDigits(string text)
    {
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}
