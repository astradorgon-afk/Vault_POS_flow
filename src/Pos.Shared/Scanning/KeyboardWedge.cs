namespace Pos.Shared.Scanning;

/// <summary>One key a keyboard-wedge scanner (or a person) typed.</summary>
/// <param name="Character">The character typed.</param>
/// <param name="AtMilliseconds">When, on a monotonic clock in milliseconds.</param>
public readonly record struct KeyStroke(char Character, double AtMilliseconds);

/// <summary>
/// How fast typing has to be before it counts as a scanner. A USB scanner
/// "types" a code at 1–15 ms per character; slow Bluetooth ones reach about
/// 40–65 ms around capital letters; people average 80–250 ms and are uneven.
/// </summary>
/// <param name="MinimumLength">The shortest code accepted as a scan.</param>
/// <param name="MaximumAverageGapMilliseconds">The slowest average pace between characters.</param>
/// <param name="MaximumGapMilliseconds">The longest single pause inside one scan.</param>
/// <param name="IdleMilliseconds">
/// How long after the last character a scan without an Enter suffix is taken as finished.
/// </param>
public sealed record WedgeOptions(
    int MinimumLength = 4,
    double MaximumAverageGapMilliseconds = 50,
    double MaximumGapMilliseconds = 100,
    double IdleMilliseconds = 120)
{
    /// <summary>Gets the settings that suit common USB, Bluetooth and handheld scanners.</summary>
    public static WedgeOptions Default { get; } = new();
}

/// <summary>Decides whether a burst of keystrokes was a scanner or a person.</summary>
public static class KeyboardWedge
{
    /// <summary>Gets whether these keystrokes look like one barcode from a scanner.</summary>
    /// <param name="keys">The keystrokes of one burst, in order, without the Enter or Tab that ended it.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>True for a scanner, false for a person typing.</returns>
    public static bool IsScan(IReadOnlyList<KeyStroke> keys, WedgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(options);

        if (keys.Count < Math.Max(2, options.MinimumLength))
        {
            return false;
        }

        double total = 0;
        for (int i = 1; i < keys.Count; i++)
        {
            double gap = keys[i].AtMilliseconds - keys[i - 1].AtMilliseconds;
            if (gap < 0 || gap > options.MaximumGapMilliseconds)
            {
                return false;
            }

            total += gap;
        }

        if (total / (keys.Count - 1) > options.MaximumAverageGapMilliseconds)
        {
            return false;
        }

        // Scanners send printable text; a burst carrying control characters
        // is a keyboard shortcut or a paste, not a barcode.
        foreach (KeyStroke key in keys)
        {
            if (char.IsControl(key.Character) && key.Character != BarcodeText.GroupSeparator)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets the text the keystrokes spell.</summary>
    /// <param name="keys">The keystrokes.</param>
    /// <returns>The text.</returns>
    public static string Text(IEnumerable<KeyStroke> keys)
        => string.Concat((keys ?? []).Select(key => key.Character));
}
