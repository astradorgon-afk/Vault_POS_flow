using System.Globalization;
using Pos.Web.Services;

namespace Pos.Web.Components.Pages;

/// <summary>Which lines of a count sheet are on show.</summary>
public enum CountSheetFilter
{
    /// <summary>Every line in scope.</summary>
    All = 0,

    /// <summary>Lines with no quantity recorded yet — what is left to do.</summary>
    Uncounted = 1,

    /// <summary>Lines where the shelf disagrees with the ledger.</summary>
    Variances = 2,

    /// <summary>Varying lines whose product already varied here recently.</summary>
    Repeats = 3,
}

/// <summary>
/// The working state of one stock count sheet: what has been typed but not yet
/// saved, what that implies for each line, and whether the sheet can be
/// submitted.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of the page so the arithmetic an operator is trusting can be tested
/// without a browser. The figure at stake is the variance: approving it posts
/// movements against the ledger, so it has to be right on screen before anyone
/// decides anything.
/// </para>
/// <para>
/// A draft is a quantity typed or scanned but not yet sent to the server. The
/// sheet shows drafts as though they were saved — that is the point of showing
/// them — while <see cref="IsDirty"/> marks which ones still need saving.
/// </para>
/// </remarks>
public sealed class InventoryCountSheet
{
    private readonly Dictionary<int, decimal?> drafts = [];
    private IReadOnlyList<PosInventoryCountLineView> lines = [];

    /// <summary>Every line in scope, in sheet order.</summary>
    public IReadOnlyList<PosInventoryCountLineView> Lines => lines;

    /// <summary>How many lines the sheet holds.</summary>
    public int TotalLines => lines.Count;

    /// <summary>How many lines have a quantity, counting drafts.</summary>
    public int CountedLines => lines.Count(line => Quantity(line) is not null);

    /// <summary>How many lines still have no quantity.</summary>
    public int RemainingLines => TotalLines - CountedLines;

    /// <summary>
    /// Whether every line has a quantity.
    /// </summary>
    /// <remarks>
    /// The server refuses a partial submission and names the uncounted lines, so
    /// the page asks the same question before offering the button rather than
    /// spending a round trip to be told no.
    /// </remarks>
    public bool IsComplete => TotalLines > 0 && RemainingLines == 0;

    /// <summary>How many lines disagree with the ledger, counting drafts.</summary>
    public int VaryingLines => lines.Count(line => Variance(line) is { } variance && variance != 0m);

    /// <summary>
    /// The variance value netted across the sheet: what the count would do to the
    /// stock on hand overall.
    /// </summary>
    public decimal NetVarianceValue => lines.Sum(line => Value(line) ?? 0m);

    /// <summary>
    /// The variance value ignoring direction, which is the figure the approval
    /// tier is measured against, so it is the one an approver needs to see.
    /// </summary>
    public decimal AbsoluteVarianceValue => lines.Sum(line => Math.Abs(Value(line) ?? 0m));

    /// <summary>Whether anything typed or scanned has yet to reach the server.</summary>
    public bool HasUnsavedChanges => lines.Any(line => IsDirty(line.LineNo));

    /// <summary>Whether any line carries a quantity at all.</summary>
    public bool HasAnyCount => CountedLines > 0;

    /// <summary>Takes a freshly loaded sheet, discarding any drafts against the old one.</summary>
    /// <param name="sheetLines">The lines as the server holds them.</param>
    public void Load(IReadOnlyList<PosInventoryCountLineView> sheetLines)
    {
        ArgumentNullException.ThrowIfNull(sheetLines);
        lines = sheetLines;
        drafts.Clear();
        foreach (PosInventoryCountLineView line in sheetLines)
        {
            drafts[line.LineNo] = line.PhysicalQuantity;
        }
    }

    /// <summary>The quantity held for a line, draft or saved.</summary>
    /// <param name="lineNo">The line number.</param>
    /// <returns>The quantity, or <see langword="null"/> while uncounted.</returns>
    public decimal? Draft(int lineNo) => drafts.TryGetValue(lineNo, out decimal? value) ? value : null;

    /// <summary>Whether a line holds a quantity that differs from the saved one.</summary>
    /// <param name="lineNo">The line number.</param>
    /// <returns><see langword="true"/> when the line still needs saving.</returns>
    public bool IsDirty(int lineNo)
        => Draft(lineNo) != lines.FirstOrDefault(line => line.LineNo == lineNo)?.PhysicalQuantity;

    /// <summary>
    /// Records a typed quantity.
    /// </summary>
    /// <param name="lineNo">The line number.</param>
    /// <param name="raw">What the operator typed.</param>
    /// <returns>
    /// <see langword="null"/> when accepted, otherwise why it was refused and the
    /// line put back to its saved quantity.
    /// </returns>
    public string? SetDraft(int lineNo, string? raw)
    {
        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal value))
        {
            drafts[lineNo] = Math.Max(0m, decimal.Round(value, 3, MidpointRounding.ToEven));
            return null;
        }

        decimal? saved = lines.FirstOrDefault(line => line.LineNo == lineNo)?.PhysicalQuantity;
        drafts[lineNo] = saved;

        // Clearing a counted line is not the same as counting none of it, and the
        // difference matters: one leaves the sheet incomplete, the other posts a
        // write-off. The operator has to say which they meant.
        return saved is null
            ? "Enter a non-negative quantity to count this line."
            : "A saved count cannot be cleared. Enter zero if none of this product was found.";
    }

    /// <summary>Adds to a line's quantity, as one scan of one unit does.</summary>
    /// <param name="lineNo">The line number.</param>
    /// <param name="quantity">How much to add.</param>
    /// <returns>The line's new quantity.</returns>
    public decimal Increment(int lineNo, decimal quantity = 1m)
    {
        decimal counted = (Draft(lineNo) ?? 0m) + quantity;
        drafts[lineNo] = counted;
        return counted;
    }

    /// <summary>The quantity on show for a line: its draft, else what was saved.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The quantity, or <see langword="null"/> while uncounted.</returns>
    public decimal? Quantity(PosInventoryCountLineView line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Draft(line.LineNo) ?? line.PhysicalQuantity;
    }

    /// <summary>
    /// What a line's variance is with the draft included.
    /// </summary>
    /// <remarks>
    /// Reading the saved quantity here instead is what made the sheet feel
    /// broken: an operator typed 5 against a system quantity of 10 and the
    /// variance column still said nothing, because the server had not been told
    /// yet. The number that matters is the one implied by what is on screen.
    /// </remarks>
    /// <param name="line">The line.</param>
    /// <returns>Counted less system, or <see langword="null"/> while uncounted.</returns>
    public decimal? Variance(PosInventoryCountLineView line)
        => Quantity(line) is { } counted ? counted - line.SystemQuantity : null;

    /// <summary>A line's variance valued at its unit cost.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The valued variance, or <see langword="null"/> while uncounted.</returns>
    public decimal? Value(PosInventoryCountLineView line)
        => Variance(line) is { } variance ? variance * line.UnitCost : null;

    /// <summary>The lines to save: everything that carries a quantity.</summary>
    /// <returns>One request per counted line.</returns>
    public List<PosCountLineRequest> CountedRequests() => [.. lines
        .Where(line => Quantity(line) is not null)
        .Select(line => new PosCountLineRequest(line.ProductId, Quantity(line)!.Value, line.BatchId))];

    /// <summary>
    /// The lines a filter and a search term leave on show.
    /// </summary>
    /// <param name="filter">Which kind of line to keep.</param>
    /// <param name="search">Free text matched against the product's name and SKU.</param>
    /// <param name="describe">Resolves a product identifier to its SKU and name.</param>
    /// <returns>The matching lines, in sheet order.</returns>
    public IEnumerable<PosInventoryCountLineView> Visible(
        CountSheetFilter filter,
        string? search,
        Func<Guid, (string Sku, string Name)> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);

        IEnumerable<PosInventoryCountLineView> shown = filter switch
        {
            CountSheetFilter.Uncounted => lines.Where(line => Quantity(line) is null),
            CountSheetFilter.Variances => lines.Where(line => Variance(line) is { } v && v != 0m),
            CountSheetFilter.Repeats => lines.Where(line =>
                line.IsRepeatVariance && Variance(line) is { } v && v != 0m),
            _ => lines,
        };

        string term = search?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return shown;
        }

        return shown.Where(line =>
        {
            (string Sku, string Name) product = describe(line.ProductId);
            return product.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                   || product.Sku.Contains(term, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>How many lines a filter would show, for the filter's own badge.</summary>
    /// <param name="filter">The filter.</param>
    /// <returns>The number of matching lines, before any search term.</returns>
    public int CountFor(CountSheetFilter filter) => filter switch
    {
        CountSheetFilter.Uncounted => RemainingLines,
        CountSheetFilter.Variances => VaryingLines,
        CountSheetFilter.Repeats => lines.Count(line =>
            line.IsRepeatVariance && Variance(line) is { } v && v != 0m),
        _ => TotalLines,
    };
}
