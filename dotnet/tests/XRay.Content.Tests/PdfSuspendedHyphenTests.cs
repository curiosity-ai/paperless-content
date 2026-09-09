using XRay.Content.Internal.Pdf;
using XRay.Content.Types;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// Upstream <c>fix(pdf): stop welding suspended hyphens and cutting unruled bands</c>
/// (xberg-io/xberg#1581). The assembly layer judged a line-ending hyphen purely by text pattern
/// — trailing <c>-</c>, a letter before it, lowercase after — with no geometry, and is applied
/// word-to-word <em>within</em> a line. Dutch suspended hyphens were welded shut:
/// <c>onderhouds- en</c> into <c>onderhoudsen</c>, <c>CV- en</c> into <c>CVen</c>. None of the
/// results is a word, so a reader searching for "onderhoud" finds nothing.
/// </summary>
public sealed class PdfSuspendedHyphenTests
{
    private static SegmentData Seg(string text, float x, float baselineY, bool bold = false) =>
        new()
        {
            Text = text,
            X = x,
            Y = baselineY,
            Width = text.Length * 5f,
            Height = 12f,
            FontSize = 12f,
            IsBold = bold,
            BaselineY = baselineY,
        };

    private static string TextOf(InternalDocument doc) =>
        string.Join("\n", doc.Elements.Select(e => e.Text));

    /// <summary>Two runs of one line, split by a style change: the hyphen is suspended, not
    /// wrapped.</summary>
    [Fact]
    public void ASuspendedHyphenAcrossAStyleRunBoundaryIsNotWelded()
    {
        var page = new List<SegmentData>
        {
            Seg("Wij verzorgen het CV- ", 72, 700),
            Seg("en boiler onderhoud voor de installatie", 182, 700, bold: true),
        };

        var doc = PdfStructure.Build(new List<List<SegmentData>> { page });

        Assert.NotNull(doc);
        Assert.Contains("CV- en", TextOf(doc!), StringComparison.Ordinal);
        Assert.DoesNotContain("CVen", TextOf(doc!), StringComparison.Ordinal);
    }

    /// <summary>Two segments split mid-line inside one style run — the site the issue traces to
    /// the plain-join path and the same-run branch of the annotated one.</summary>
    [Fact]
    public void ASuspendedHyphenWithinOneStyleRunIsNotWelded()
    {
        var page = new List<SegmentData>
        {
            Seg("onderhouds- ", 72, 700),
            Seg("en installatiewerkzaamheden aan de ketel", 140, 700),
        };

        var doc = PdfStructure.Build(new List<List<SegmentData>> { page });

        Assert.NotNull(doc);
        Assert.Contains("onderhouds- en", TextOf(doc!), StringComparison.Ordinal);
        Assert.DoesNotContain("onderhoudsen", TextOf(doc!), StringComparison.Ordinal);
    }

    /// <summary>
    /// The control from the issue's own reproducer: a genuine wrapped-line hyphen, where the two
    /// runs sit on different baselines, must still be rejoined. A fix that simply stopped all
    /// dehyphenation would pass the two tests above for the wrong reason.
    /// </summary>
    [Fact]
    public void AGenuineLineWrapHyphenStillJoins()
    {
        var page = new List<SegmentData>
        {
            Seg("Zie voor meer details de installatie-", 72, 700),
            Seg("handleiding van de fabrikant hierover", 72, 686),
        };

        var doc = PdfStructure.Build(new List<List<SegmentData>> { page });

        Assert.NotNull(doc);
        Assert.Contains("installatiehandleiding", TextOf(doc!), StringComparison.Ordinal);
    }
}
