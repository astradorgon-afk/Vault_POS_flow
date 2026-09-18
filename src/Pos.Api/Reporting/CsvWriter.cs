using System.Globalization;
using System.Reflection;
using System.Text;

namespace Pos.Api.Reporting;

/// <summary>
/// Turns report rows into CSV.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than pulled in as a dependency: the rows are flat
/// records of primitives, and the whole job is quoting and a formula guard.
/// </para>
/// <para>
/// A spreadsheet treats a cell beginning <c>=</c>, <c>+</c>, <c>-</c> or
/// <c>@</c> as a formula. A product named <c>=cmd|…</c>, entered by anyone who
/// can name a product, would then run when a manager opened the export. Every such
/// cell is prefixed with an apostrophe, which spreadsheets read as "this is text"
/// and strip on display.
/// </para>
/// </remarks>
public static class CsvWriter
{
    /// <summary>Writes a header row and one row per item.</summary>
    /// <typeparam name="T">The row type; its public properties become the columns.</typeparam>
    /// <param name="rows">The rows.</param>
    /// <returns>The CSV, ready to send.</returns>
    public static string Write<T>(IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        PropertyInfo[] columns = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        StringBuilder csv = new();

        csv.AppendLine(string.Join(',', columns.Select(c => Escape(c.Name))));

        foreach (T row in rows)
        {
            csv.AppendLine(string.Join(',', columns.Select(c => Escape(Render(c.GetValue(row))))));
        }

        return csv.ToString();
    }

    /// <summary>
    /// Renders one value in a form a spreadsheet and a script both read the same
    /// way.
    /// </summary>
    /// <remarks>
    /// Dates and times go out in round-trip form and decimals with the invariant
    /// separator. An export formatted for the server's locale is one that changes
    /// meaning when the server moves.
    /// </remarks>
    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        DateTimeOffset at => at.ToString("O", CultureInfo.InvariantCulture),
        DateTime at => at.ToString("O", CultureInfo.InvariantCulture),
        DateOnly on => on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Escape(string value)
    {
        // The formula guard comes first, so the apostrophe ends up inside the
        // quotes rather than outside them.
        string cell = value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;

        return cell.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : cell;
    }
}
