using XRay.Content.Internal.Pdf;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// The document-scoped witness evidence the PDF text repairs use to tell a genuine word or hyphen
/// apart from an extraction artefact that reads identically at the string layer. Ports upstream's
/// <c>fix(pdf): keep lexical hyphens attested elsewhere in the document</c> (#1543) and
/// <c>fix(pdf,docx): stop welding words …</c> (#1591).
/// </summary>
public sealed class PdfTextRepairWitnessTests
{
    private static PdfTextRepairWitnesses From(params string[] segmentTexts)
    {
        var page = new List<SegmentData>();
        foreach (var text in segmentTexts) page.Add(new SegmentData { Text = text });
        return PdfTextRepairWitnesses.Collect(new List<List<SegmentData>> { page });
    }

    // ── ligature-space repair (#1591) ─────────────────────────────────────────

    /// <summary>With no evidence either way, the gap reads as a decomposed ligature and closes.
    /// This is the fix's known false positive, documented rather than silently accepted.</summary>
    [Fact]
    public void AnUnwitnessedWordBoundaryStillWelds()
    {
        Assert.Equal(
            "first efficiently significant flange",
            PdfTextRepair.RepairLigatureSpaces(
                "f irst eff iciently signif icant f lange", PdfTextRepairWitnesses.Empty));

        Assert.Equal(
            "relieffor the victims",
            PdfTextRepair.RepairLigatureSpaces("relief for the victims", PdfTextRepairWitnesses.Empty));
    }

    /// <summary>
    /// When either fragment is attested elsewhere in the document, the space is a real word
    /// boundary and survives. The static 33-word English list this replaces only ever checked the
    /// left fragment, so "relief for" and "itself infringes" both welded.
    /// </summary>
    [Theory]
    [InlineData("relief for the victims", "the relief was granted")]
    [InlineData("itself infringes the patent", "the claim itself stands")]
    // The issue's Dutch reproducer: "bedrijf is" and "bedrijf komen" have identical gap
    // geometry, glyph height and font — only the witness tells them apart.
    [InlineData("bedrijf is gesloten", "het bedrijf komen")]
    public void AWitnessedFragmentKeepsItsWordBoundary(string text, string elsewhereInTheDocument)
    {
        var witnesses = From(elsewhereInTheDocument);

        Assert.Equal(text, PdfTextRepair.RepairLigatureSpaces(text, witnesses));
    }

    /// <summary>A witness on the right fragment is equally sufficient.</summary>
    [Fact]
    public void AWitnessOnTheRightFragmentIsEnough()
    {
        var witnesses = From("follow these instructions carefully");

        Assert.Equal("of instructions", PdfTextRepair.RepairLigatureSpaces("of instructions", witnesses));
    }

    /// <summary>
    /// A fragment must not witness itself: the tokens on either side of a candidate pattern are
    /// skipped when collecting, or every candidate would witness itself from its own occurrence
    /// and the repair would never fire.
    /// </summary>
    [Fact]
    public void ACandidatesOwnFragmentsAreNotEvidenceForIt()
    {
        var witnesses = From("f irst");

        Assert.DoesNotContain("irst", witnesses.Words);
        Assert.Equal("first", PdfTextRepair.RepairLigatureSpaces("f irst", witnesses));
    }

    /// <summary>A single letter is too common to be evidence — the bare <c>f</c> of "f irst"
    /// would otherwise witness itself.</summary>
    [Fact]
    public void ASingleLetterIsNotAWitness()
    {
        var witnesses = From("f g h");

        Assert.False(witnesses.IsWitnessedWord("f"));
        Assert.Equal("first", PdfTextRepair.RepairLigatureSpaces("f irst", witnesses));
    }

    // ── hyphen witnesses (#1543) ──────────────────────────────────────────────

    /// <summary>
    /// A hyphen appearing mid-run witnesses a genuine authored compound, because a line-wrap
    /// hyphen is by construction the <em>last</em> character of its segment.
    /// </summary>
    [Fact]
    public void AMidRunHyphenWitnessesACompound()
    {
        var witnesses = From("the price-determining factors apply");

        Assert.Contains(("price", "determining"), witnesses.Hyphens);
    }

    /// <summary>A trailing hyphen is the artefact under judgment, so it cannot witness itself.</summary>
    [Fact]
    public void ATrailingHyphenIsNotAWitness()
    {
        var witnesses = From("the soft-");

        Assert.DoesNotContain(("the", "soft"), witnesses.Hyphens);
        Assert.Empty(witnesses.Hyphens);
    }

    /// <summary>Single letters on either side are initials or bullet dashes, not compounds.</summary>
    [Fact]
    public void ASingleLetterSideDoesNotMintAWitness()
    {
        var witnesses = From("J-P Sartre and the x-axis label");

        Assert.DoesNotContain(("j", "p"), witnesses.Hyphens);
        Assert.DoesNotContain(("x", "axis"), witnesses.Hyphens);
    }

    /// <summary>Witnesses are gathered across the whole document: evidence on one page is what
    /// settles a break on another.</summary>
    [Fact]
    public void WitnessesSpanEveryPage()
    {
        var witnesses = PdfTextRepairWitnesses.Collect(new List<List<SegmentData>>
        {
            new() { new SegmentData { Text = "opening page" } },
            new() { new SegmentData { Text = "the price-determining factors" } },
        });

        Assert.Contains(("price", "determining"), witnesses.Hyphens);
        Assert.Contains("opening", witnesses.Words);
    }
}
