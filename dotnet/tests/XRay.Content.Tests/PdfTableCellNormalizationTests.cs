using XRay.Content.Internal.Pdf;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// Upstream <c>fix(pdf): normalize table cells only in numeric columns</c>
/// (xberg-io/xberg#1582). The data-cell normalizer rewrites dashes to ASCII hyphens, collapses
/// the spaces around them, lowercases <c>E-</c>/<c>E+</c> and empties a lone-dash cell. That is
/// right for a financial column and corrupts prose — and it ran on every data cell of every
/// reconstructed table.
/// </summary>
public sealed class PdfTableCellNormalizationTests
{
    /// <summary>A prose column with one cell under test, alongside a genuinely numeric price
    /// column so the table is accepted as a table at all.</summary>
    private static List<List<string>> ProseTable(string cellUnderTest) => new()
    {
        new() { "Line", "Part Description", "Unit Price" },
        new() { "1.", cellUnderTest, "$4.25" },
        new() { "2.", "Anodized Aluminum Bracket", "$12.90" },
        new() { "3.", "Rubber Grommet Assembly", "$1.15" },
        new() { "4.", "Tempered Glass Panel", "$38.00" },
    };

    /// <summary>
    /// An em-dash welded to a leader in a title, a part code split by a spaced hyphen, and a
    /// lone em-dash standing for an unfilled field: each means something in a document, and each
    /// was rewritten, lowercased or emptied outright.
    /// </summary>
    [Theory]
    [InlineData("Functionaliteit—12")]
    [InlineData("Montagebeugel HRE - HReco")]
    [InlineData("—")]
    public void AProseCellSurvivesTableNormalization(string cell)
    {
        var processed = PdfTableReconstruct.PostProcessTable(
            ProseTable(cell), layoutGuided: true, allowSingleColumn: false);

        Assert.NotNull(processed);
        Assert.Equal(cell, processed![1][1]);
    }

    /// <summary>
    /// A genuine financial table must keep the full normalization: an em-dash nil cell emptied,
    /// <c>"- 3"</c> joined to <c>"-3"</c>, and a scientific-notation exponent lowercased. A gate
    /// that simply switched the normalizer off would pass the tests above for the wrong reason.
    /// </summary>
    [Fact]
    public void ANumericColumnIsNormalizedExactlyAsBefore()
    {
        var table = new List<List<string>>
        {
            new() { "Item", "2024", "2023" },
            new() { "Omzet", "1234", "1100" },
            new() { "Bijzondere baten", "—", "- 3" },
            new() { "Meetfout", "1.5E-05", "2.0E-06" },
            new() { "Afschrijving", "-12", "-9" },
        };

        var processed = PdfTableReconstruct.PostProcessTable(
            table, layoutGuided: true, allowSingleColumn: false);

        Assert.NotNull(processed);
        Assert.Equal(new List<string> { "Bijzondere baten", "", "-3" }, processed![2]);
        Assert.Equal(new List<string> { "Meetfout", "1.5e-05", "2.0e-06" }, processed[3]);
        Assert.Equal(new List<string> { "Afschrijving", "-12", "-9" }, processed[4]);
    }
}

/// <summary>
/// Upstream <c>fix extraction regressions reported in open issues</c> (xberg-io/xberg#1558):
/// table reconstruction dropped early rows when data-start inference classified more than two
/// leading rows as headers. The two-row cap is right; discarding what it cut is not.
/// </summary>
public sealed class PdfTableHeaderCapTests
{
    [Fact]
    public void SurplusInferredHeaderRowsAreDemotedNotDropped()
    {
        var table = new List<List<string>>();
        for (int row = 1; row <= 18; row++)
            table.Add(new List<string>
            {
                $"{row} 8000{row:D2}",
                "Fastening screw",
                $"{row},10",
                row == 7 ? "package of 30" : "available",
            });

        var processed = PdfTableReconstruct.PostProcessTable(
            table, layoutGuided: true, allowSingleColumn: false);

        Assert.NotNull(processed);
        string flattened = string.Join(" ", processed!.SelectMany(r => r));
        for (int row = 1; row <= 18; row++)
        {
            string article = $"8000{row:D2}";
            int occurrences = 0;
            for (int i = flattened.IndexOf(article, StringComparison.Ordinal); i >= 0;
                 i = flattened.IndexOf(article, i + 1, StringComparison.Ordinal))
                occurrences++;
            Assert.Equal(1, occurrences);
        }
    }
}
