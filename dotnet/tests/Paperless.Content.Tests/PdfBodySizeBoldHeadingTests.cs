using Paperless.Content.Internal.Pdf;
using Xunit;

namespace Paperless.Content.Tests;

/// <summary>
/// Upstream's chain of body-size bold heading fixes: <c>fix(pdf): classify repeated bold body-size
/// headings</c>, <c>… keep subordinate bold text out of headings</c>, <c>… preserve agenda heading
/// hierarchy</c>, <c>… narrow same-row heading suppression</c> and <c>… reject invalid heading
/// geometry</c>. A document whose section titles are set in the body face and distinguished only by
/// weight gives font-size clustering nothing to work with, and its whole outline is lost.
/// </summary>
public sealed class PdfBodySizeBoldHeadingTests
{
    private const float BodyFontSize = 12f;

    private static PdfParagraph Para(
        string text, bool isBold, byte? headingLevel = null,
        float left = 74f, float bottom = 0f, float top = 12f)
    {
        var segment = new SegmentData
        {
            Text = text,
            X = left,
            Y = bottom,
            Width = 200f,
            Height = BodyFontSize,
            FontSize = BodyFontSize,
            IsBold = isBold,
            BaselineY = bottom,
        };
        return new PdfParagraph
        {
            Text = text,
            Lines = new List<PdfLine>
            {
                new() { Segments = new List<SegmentData> { segment }, BaselineY = bottom, DominantFontSize = BodyFontSize, IsBold = isBold },
            },
            DominantFontSize = BodyFontSize,
            IsBold = isBold,
            HeadingLevel = headingLevel,
            WordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            BlockBbox = (left, bottom, left + 200f, top),
        };
    }

    /// <summary>A document with an existing conventional outline, to sit the candidates beside.</summary>
    private static List<List<PdfParagraph>> WithExistingOutline() => new()
    {
        new() { Para("Existing document title", true, 1), Para("Existing section heading", true, 2) },
    };

    /// <summary>
    /// A title aligned with, and above, the block of body text it opens is a real section heading —
    /// and once enough of them repeat, all of them are.
    /// </summary>
    [Fact]
    public void RepeatedAlignedBoldTitlesArePromoted()
    {
        var pages = WithExistingOutline();
        for (int i = 0; i < 9; i++)
            pages.Add(new List<PdfParagraph>
            {
                Para($"Genuine repeated section heading {i}", true, bottom: 700f, top: 712f),
                Para($"Section {i} opens a block of ordinary body text.", false, bottom: 660f, top: 690f),
            });

        PdfBodySizeBoldHeadings.Promote(pages, BodyFontSize);

        Assert.Equal(9, pages.Skip(1).Count(page => page[0].HeadingLevel == 3));
        // The conventional outline above is left exactly as it was.
        Assert.Equal((byte?)1, pages[0][0].HeadingLevel);
        Assert.Equal((byte?)2, pages[0][1].HeadingLevel);
    }

    /// <summary>
    /// A bold line indented past the block beneath it is subordinate to that block — a presenter
    /// attribution, a caption — not a title for it.
    /// </summary>
    [Fact]
    public void IndentedAttributionsAreNotPromoted()
    {
        var pages = WithExistingOutline();
        for (int i = 0; i < 16; i++)
            pages.Add(new List<PdfParagraph>
            {
                Para($"Presenter Name {i}, Department Director", true, left: 126f, bottom: 700f, top: 712f),
                Para($"Agenda item {i} discussion continues in ordinary body text.", false,
                    left: 74f, bottom: 660f, top: 690f),
            });

        PdfBodySizeBoldHeadings.Promote(pages, BodyFontSize);

        Assert.Equal(0, pages.Skip(1).Count(page => page[0].HeadingLevel == 3));
        Assert.Equal((byte?)1, pages[0][0].HeadingLevel);
        Assert.Equal((byte?)2, pages[0][1].HeadingLevel);
    }

    /// <summary>
    /// Two blocks that overlap vertically are reading as one row, so the bold half is a run-in
    /// label rather than a title — even when it carries explicit section numbering.
    /// </summary>
    [Fact]
    public void ARunInLabelSharingItsRowIsNotPromoted()
    {
        var pages = WithExistingOutline();
        for (int i = 1; i <= 9; i++)
            pages.Add(new List<PdfParagraph>
            {
                Para($"{i}. Onderwerp", true, left: 74f, bottom: 700f, top: 712f),
                Para($"toelichting bij punt {i} in gewone tekst", false, left: 280f, bottom: 700f, top: 712f),
            });

        PdfBodySizeBoldHeadings.Promote(pages, BodyFontSize);

        Assert.Equal(0, pages.Skip(1).Count(page => page[0].HeadingLevel == 3));
    }

    /// <summary>
    /// A document that already has an outline of its own is not re-headed from weight: the
    /// candidates have to outnumber what other means already found.
    /// </summary>
    [Fact]
    public void AFewBoldLinesBesideARealOutlineChangeNothing()
    {
        var pages = new List<List<PdfParagraph>>
        {
            new()
            {
                Para("Title", true, 1), Para("Section one", true, 2), Para("Section two", true, 2),
                Para("Section three", true, 2), Para("Section four", true, 2),
            },
        };
        for (int i = 0; i < 3; i++)
            pages.Add(new List<PdfParagraph>
            {
                Para($"Bold run-in phrase {i}", true, bottom: 700f, top: 712f),
                Para($"followed by ordinary body text {i} that runs on.", false, bottom: 660f, top: 690f),
            });

        PdfBodySizeBoldHeadings.Promote(pages, BodyFontSize);

        Assert.Equal(0, pages.Skip(1).Count(page => page[0].HeadingLevel == 3));
    }

    /// <summary>Geometry that is not finite cannot support the measurement, so nothing is
    /// promoted on it.</summary>
    [Fact]
    public void NonFiniteGeometryBlocksPromotion()
    {
        var pages = WithExistingOutline();
        for (int i = 0; i < 9; i++)
        {
            var heading = Para($"Genuine repeated section heading {i}", true, bottom: 700f, top: 712f);
            heading.BlockBbox = (float.NaN, 700f, 274f, 712f);
            pages.Add(new List<PdfParagraph>
            {
                heading,
                Para($"Section {i} opens a block of ordinary body text.", false, bottom: 660f, top: 690f),
            });
        }

        PdfBodySizeBoldHeadings.Promote(pages, BodyFontSize);

        Assert.Equal(0, pages.Skip(1).Count(page => page[0].HeadingLevel == 3));
    }

    /// <summary>One or two bold words are far more often a run-in emphasis than a section title.
    /// </summary>
    [Fact]
    public void AOneWordBoldLineIsNotACandidate()
    {
        var pages = WithExistingOutline();
        for (int i = 0; i < 9; i++)
            pages.Add(new List<PdfParagraph>
            {
                Para("Datalekprotocol", true, bottom: 700f, top: 712f),
                Para($"Section {i} opens a block of ordinary body text.", false, bottom: 660f, top: 690f),
            });

        PdfBodySizeBoldHeadings.Promote(pages, BodyFontSize);

        Assert.Equal(0, pages.Skip(1).Count(page => page[0].HeadingLevel == 3));
    }
}
