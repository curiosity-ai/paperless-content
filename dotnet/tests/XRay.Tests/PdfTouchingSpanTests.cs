using XRay.Internal.Pdf;
using Xunit;

namespace XRay.Tests;

/// <summary>
/// Upstream <c>fix(pdf): stop splitting a word across two touching spans</c>
/// (xberg-io/xberg#1566). A word drawn as two spans 0.069 pt apart — 0.008 em at 9 pt, against
/// a 2.5 pt space glyph — came back as "pri js" instead of "prijs". No gap threshold could
/// produce that space, because the style comparison returns first: a mid-word switch between
/// two embedded subset fonts whose descriptors disagree reads as a style change with no
/// geometric signal at all. That is why it never reproduced against base-14 Helvetica, and why
/// widening the gap tolerance changed nothing.
/// </summary>
public sealed class PdfTouchingSpanTests
{
    private static SegmentData Seg(string text, float x, float width, float fontSize, float baselineY) =>
        new()
        {
            Text = text,
            X = x,
            Y = baselineY,
            Width = width,
            Height = fontSize,
            FontSize = fontSize,
            BaselineY = baselineY,
        };

    /// <summary>The reported case, at its measured geometry.</summary>
    [Fact]
    public void TouchingSpansWithDifferingStyleStayJoined()
    {
        var prev = Seg("2 per ketel, pri", 287.864f, 46.631f, 8.999996f, 331.641f);
        var next = Seg("js per meter", 334.564f, 41.161f, 8.999996f, 331.641f);
        prev.IsBold = false;
        next.IsBold = true;

        Assert.True(PdfStructure.SegmentsAreTouching(prev, "pri", next, "js"));
    }

    /// <summary>
    /// The space glyph's advance sits inside the previous segment's width, so the two segments
    /// measure as touching — but the producer drew that space and it must survive. The words are
    /// whitespace-split and so cannot reveal it; only the segment text can.
    /// </summary>
    [Fact]
    public void TheGuardNeverSwallowsAnExplicitlyDrawnSpace()
    {
        var prev = Seg("alpha ", 10f, 30f, 9f, 100f);
        var next = Seg("beta", 40f, 20f, 9f, 100f);
        next.IsBold = true;

        Assert.False(PdfStructure.SegmentsAreTouching(prev, "alpha", next, "beta"));
    }

    /// <summary>
    /// The rest of the pipeline treats a 0.05 em gap as a genuine word boundary. A guard sitting
    /// at that bound would flip on how <c>fontSize * ratio</c> rounds, so the margin has to hold
    /// at a small font size too — a threshold that flips on font size is not a threshold.
    /// </summary>
    [Theory]
    [InlineData(9f)]
    [InlineData(20f)]
    public void TheGuardStaysBelowTheWordBoundaryGapAtEveryFontSize(float fontSize)
    {
        float gap = fontSize * 0.05f;
        var prev = Seg("plain", 10f, 20f, fontSize, 100f);
        var next = Seg("bold", 10f + 20f + gap, 20f, fontSize, 100f);
        next.IsBold = true;

        Assert.False(PdfStructure.SegmentsAreTouching(prev, "plain", next, "bold"));
    }

    /// <summary>A different baseline is a different line, whatever the horizontal gap says.</summary>
    [Fact]
    public void SpansOnDifferentBaselinesAreNotTouching()
    {
        var prev = Seg("pri", 100f, 15f, 9f, 100f);
        var next = Seg("js", 115f, 10f, 9f, 88f);

        Assert.False(PdfStructure.SegmentsAreTouching(prev, "pri", next, "js"));
    }

    /// <summary>A non-word character on either side of the boundary is a real separator.</summary>
    [Fact]
    public void APunctuationBoundaryIsNotTouching()
    {
        var prev = Seg("total:", 100f, 20f, 9f, 100f);
        var next = Seg("42", 120f, 10f, 9f, 100f);

        Assert.False(PdfStructure.SegmentsAreTouching(prev, "total:", next, "42"));
    }

    /// <summary>
    /// The table path reads integer-rounded words that carry neither font size nor baseline, so
    /// a sub-point gap is not representable once words exist: the join has to happen while the
    /// segments are still in hand, or the cell-text join re-inserts the space.
    /// </summary>
    [Fact]
    public void TheTablePathJoinsATouchingSpanIntoOneWord()
    {
        var prev = Seg("2 per ketel, pri", 287.864f, 46.631f, 8.999996f, 331.641f);
        var next = Seg("js per meter", 334.564f, 41.161f, 8.999996f, 331.641f);
        prev.IsBold = false;
        next.IsBold = true;

        var words = PdfTableReconstruct.SegmentsToWords(new List<SegmentData> { prev, next }, 792f);

        Assert.Contains(words, w => w.Text == "prijs");
        Assert.DoesNotContain(words, w => w.Text is "pri" or "js");
    }
}
