using FluentAssertions;
using Pos.Api.Reporting;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// The CSV writer. Quoting is routine; the formula guard is not, and it is the
/// reason this is written by hand rather than assumed.
/// </summary>
public sealed class CsvWriterTests
{
    [Fact]
    public void AHeaderIsWritten_EvenWithNoRows()
    {
        // An empty period downloads a file that says what the columns were, rather
        // than an empty one that says nothing.
        CsvWriter.Write<Row>([]).Should().Be("Name,Quantity,When,Flag,Missing" + Environment.NewLine);
    }

    [Fact]
    public void CommasQuotesAndNewlinesAreQuoted()
    {
        string csv = CsvWriter.Write([Sample with { Name = "Biscuits, 200g \"value\"\nline two" }]);

        csv.Should().Contain("\"Biscuits, 200g \"\"value\"\"\nline two\"");
    }

    [Fact]
    public void ACellThatLooksLikeAFormulaIsNeutralised()
    {
        // A spreadsheet runs a cell beginning = + - or @. A product named
        // "=cmd|…", entered by anybody who can name a product, would otherwise run
        // when a manager opened the export.
        foreach (string dangerous in new[] { "=cmd|'/c calc'!A1", "+1+1", "-2+3", "@SUM(A1)" })
        {
            string csv = CsvWriter.Write([Sample with { Name = dangerous }]);

            csv.Should().Contain("'" + dangerous[0], "the apostrophe tells a spreadsheet this is text");
        }
    }

    [Fact]
    public void ValuesAreWrittenInvariantly()
    {
        string csv = CsvWriter.Write([Sample]);

        // Round-trip timestamps and an invariant decimal separator: an export
        // formatted for the server's locale changes meaning when the server moves.
        csv.Should().Contain("1234.5600");
        csv.Should().Contain("2026-09-18T08:00:00.0000000+00:00");
        csv.Should().Contain("true");
    }

    [Fact]
    public void NullsAreEmptyRatherThanTheWordNull()
    {
        string csv = CsvWriter.Write([Sample]);

        csv.TrimEnd().Should().EndWith(",", "the last column is null, and a reader should see nothing there");
    }

    private static readonly Row Sample = new(
        "Biscuits",
        1234.5600m,
        new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero),
        true,
        null);

    private sealed record Row(string Name, decimal Quantity, DateTimeOffset When, bool Flag, string? Missing);
}
