using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xberg.Internal.Doc;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// Legacy <c>.doc</c> paragraph properties: list membership, nesting depth and styled headings,
/// read from the <c>PAPX</c> layer. Ports upstream's <c>extractors/doc.rs</c> corpus tests
/// (xberg-io/xberg#1550, #1553), which run against real Word documents because the structures
/// involved — <c>PlcfBtePapx</c>, <c>PapxFkp</c>, <c>PlfLst</c>, <c>PlfLfo</c>, the style sheet —
/// are far too interlocking to synthesize convincingly.
/// </summary>
public sealed class DocPapxTests
{
    private const string DocMime = "application/msword";

    /// <summary>
    /// Corpus fixtures are required, not optional. A test that returns early when its fixture is
    /// missing passes without asserting anything, which is how this suite's PDF tests sat green
    /// for a whole session while the corpus was absent.
    /// </summary>
    private static InternalDocument Extract(string relative)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "../../../../../../test_documents", relative);
        Assert.True(File.Exists(path),
            $"corpus fixture missing at {relative}; run `git submodule update --init test_documents` "
            + "and `python3 test_documents/scripts/fetch_corpus.py` rather than skipping");
        return new DocExtractor().Extract(File.ReadAllBytes(path), DocMime, new ExtractionConfig());
    }

    private const string ListsFixture = "doc/unit_test_lists.doc";

    /// <summary>
    /// <c>ordered</c> distinguishes a numbered list from a bulleted one, and is resolved from the
    /// list tables' <c>nfc</c> rather than assumed.
    /// </summary>
    /// <remarks>
    /// A reader that never resolved <c>nfc</c> and fell back to a constant would emit one kind for
    /// everything and still look entirely plausible — twelve ordered lists and zero bulleted reads
    /// as a clean result, not as a lookup that never ran. That is exactly what happens while the
    /// <c>LVL</c> blocks are sliced off at <c>lcbPlfLst</c>, because they live <em>past</em> that
    /// length.
    /// <para>
    /// FIXTURE REQUIREMENT: this can only fail if the document contains <strong>both</strong>
    /// kinds. <c>unit_test_lists.doc</c> carries <c>nfc</c> 0 (arabic) and <c>nfc</c> 23 (bullet).
    /// Swapping in a bullets-only or numbers-only document disarms the guard silently: it keeps
    /// passing while <c>nfc</c> resolution is dead. The property that makes this test able to fail
    /// belongs to the corpus, not to the code, so it is recorded next to the assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public void ListContainersCarryTheKindResolvedFromNfc()
    {
        var doc = Extract(ListsFixture);

        int ordered = 0, bulleted = 0;
        foreach (var element in doc.Elements)
        {
            if (element.Kind.Tag != ElementKindTag.ListStart) continue;
            if (element.Kind.Ordered == true) ordered++;
            else bulleted++;
        }

        Assert.True(ordered > 0 && bulleted > 0,
            $"the document mixes nfc 0 and nfc 23, so both container kinds must appear; got "
            + $"{ordered} ordered and {bulleted} bulleted — a single kind means nfc was never read");
    }

    /// <summary>Every opened list container is closed, at the right nesting depth.</summary>
    [Fact]
    public void ListContainersAreBalanced()
    {
        var doc = Extract(ListsFixture);

        int depth = 0;
        foreach (var element in doc.Elements)
        {
            if (element.Kind.Tag == ElementKindTag.ListStart) depth++;
            else if (element.Kind.Tag == ElementKindTag.ListEnd)
            {
                depth--;
                Assert.True(depth >= 0, "a list was closed that had not been opened");
            }
        }
        Assert.Equal(0, depth);
    }

    /// <summary>
    /// A document that applies heading styles is taken at its word, and the level comes from the
    /// style rather than being a fixed h2.
    /// </summary>
    /// <remarks>
    /// FIXTURE REQUIREMENT: <c>unit_test_lists.doc</c> applies <c>heading 1</c> and
    /// <c>heading 3</c> to seven paragraphs that are NOT list-bound. A fixture whose styled
    /// headings were all list-bound would emit them as list items and exercise the fallback
    /// instead, passing the other tests here while leaving this one asserting nothing.
    /// </remarks>
    [Fact]
    public void ADocumentThatAppliesHeadingStylesUsesThemAndTheirLevels()
    {
        var doc = Extract(ListsFixture);

        var levels = doc.Elements
            .Where(e => e.Kind.Tag == ElementKindTag.Heading)
            .Select(e => e.Kind.Level)
            .ToList();

        Assert.Equal(7, levels.Count);
        Assert.Contains(levels, level => level == 1);
        Assert.Contains(levels, level => level == 3);
        // h2 is what the shape heuristic emits for everything; seeing it here means the heuristic
        // ran instead of the styles.
        Assert.DoesNotContain(levels, level => level == 2);
    }

    /// <summary>
    /// A document whose only heading-styled paragraph is list-bound emits no styled heading at
    /// all, so it must fall back to shape rather than end up with no heading structure.
    /// </summary>
    /// <remarks>
    /// <c>simple.doc</c> is exactly that: its <c>Heading 1</c> paragraph also carries a list
    /// binding, and a list binding wins (matching the DOCX path's handling of <c>w:numPr</c>).
    /// Counting it as "this document uses heading styles" suppressed the heuristic while emitting
    /// nothing in its place.
    /// </remarks>
    [Fact]
    public void ADocumentWhoseOnlyStyledHeadingIsListBoundFallsBackToShape()
    {
        var doc = Extract("vendored/unstructured/doc/simple.doc");

        int headings = doc.Elements.Count(e => e.Kind.Tag == ElementKindTag.Heading);

        Assert.Equal(3, headings);
    }

    /// <summary>A document with no list bindings must not gain list structure.</summary>
    [Fact]
    public void AListFreeDocumentEmitsNoListElements()
    {
        var doc = Extract("vendored/unstructured/doc/fake.doc");

        Assert.DoesNotContain(doc.Elements, e =>
            e.Kind.Tag is ElementKindTag.ListStart or ElementKindTag.ListEnd or ElementKindTag.ListItem);
    }

    /// <summary>
    /// Elements follow Word's own paragraph marks, not blank lines. Word's paragraph mark is a
    /// single CR, so any two consecutive non-empty paragraphs without a blank line between them
    /// used to arrive as one element — this document returned its entire body as a single
    /// element (xberg-io/xberg#1550).
    /// </summary>
    [Fact]
    public void ElementsSplitOnParagraphMarksNotBlankLines()
    {
        var doc = Extract("doc/vendor_renewal_letter.doc");

        int bodyElements = doc.Elements.Count(e =>
            e.Kind.Tag is ElementKindTag.Paragraph or ElementKindTag.Heading);

        Assert.True(bodyElements >= 8,
            $"expected Word's own paragraph granularity; got {bodyElements} elements, which is the "
            + "blank-line chunking this replaced");
    }

    /// <summary>
    /// The same document, read as a whole, still normalizes in one pass: content is not derived
    /// from per-paragraph normalization, because the field-code stack carries across the string.
    /// </summary>
    [Fact]
    public void DuplicateParagraphsAreEachTheirOwnElement()
    {
        var doc = Extract("vendored/unstructured/doc/duplicate-paragraphs.doc");

        int bodyElements = doc.Elements.Count(e =>
            e.Kind.Tag is ElementKindTag.Paragraph or ElementKindTag.Heading);

        Assert.True(bodyElements >= 5,
            $"the document's repeated paragraphs must each be their own element; got {bodyElements}");
    }


    // ── style base chain, which no corpus fixture exercises ───────────────────

    /// <summary>
    /// Build a table stream holding one <c>STSH</c>, and the FIB bytes that point at it, so the
    /// style-sheet reader can be driven without a whole document.
    /// </summary>
    /// <param name="styles">Each style's <c>sti</c> and <c>istdBase</c>, in <c>istd</c> order.</param>
    private static (byte[] WordDoc, byte[] TableStream) StyleSheetOnly(params (ushort Sti, ushort IstdBase)[] styles)
    {
        // STSH: cbStshi, then STSHI (whose first field is cstd), then one LPStd per style —
        // a 16-bit length followed by that many bytes, of which the first four are STDFBase.
        const int CbStshi = 2;
        var stsh = new List<byte>();
        stsh.AddRange(BitConverter.GetBytes((ushort)CbStshi));
        stsh.AddRange(BitConverter.GetBytes((ushort)styles.Length));
        foreach (var (sti, istdBase) in styles)
        {
            stsh.AddRange(BitConverter.GetBytes((ushort)4));
            stsh.AddRange(BitConverter.GetBytes((ushort)(sti & 0x0FFF)));
            stsh.AddRange(BitConverter.GetBytes((ushort)((istdBase & 0x0FFF) << 4)));
        }

        const int Fc = 16;
        var table = new List<byte>(new byte[Fc]);
        table.AddRange(stsh);

        // `fcStshf` is FibRgFcLcb97 pair 1; the reader is handed the array's own offset, so the
        // FIB can start there.
        var wordDoc = new byte[8 * 8];
        BitConverter.TryWriteBytes(wordDoc.AsSpan(1 * 8), (uint)Fc);
        BitConverter.TryWriteBytes(wordDoc.AsSpan(1 * 8 + 4), (uint)stsh.Count);
        return (wordDoc, table.ToArray());
    }

    /// <summary>
    /// A custom style derived from a built-in heading is still a heading, so resolution follows
    /// <c>istdBase</c> rather than testing <c>sti</c> 1..9 outright. Upstream's case is a corpus
    /// document carrying <c>TOC Heading</c> — <c>sti</c> 46, based on <c>heading 1</c>.
    /// </summary>
    /// <remarks>
    /// None of the six <c>.doc</c> fixtures exercises this: removing the base-chain walk leaves
    /// every corpus test above green. It is covered here instead, against a synthesized sheet.
    /// </remarks>
    [Fact]
    public void ACustomStyleDerivedFromAHeadingIsStillAHeading()
    {
        // istd 0: heading 1. istd 1: `TOC Heading`, sti 46, based on istd 0.
        var (wordDoc, table) = StyleSheetOnly((Sti: 1, IstdBase: 0x0FFF), (Sti: 46, IstdBase: 0));
        var sheet = DocPapx.StyleSheet.Build(wordDoc, table, 0);

        Assert.Equal((byte)1, sheet.HeadingLevel(0));
        Assert.Equal((byte)1, sheet.HeadingLevel(1));
    }

    /// <summary>A style based on an ordinary style is not a heading, however deep the chain.</summary>
    [Fact]
    public void AStyleDerivedFromOrdinaryTextIsNotAHeading()
    {
        // istd 0: Normal (sti 0). istd 1: a custom style based on it.
        var (wordDoc, table) = StyleSheetOnly((Sti: 0, IstdBase: 0x0FFF), (Sti: 46, IstdBase: 0));
        var sheet = DocPapx.StyleSheet.Build(wordDoc, table, 0);

        Assert.Null(sheet.HeadingLevel(1));
    }

    /// <summary>
    /// A sheet whose base chain points at itself, or cycles, must terminate rather than spin.
    /// </summary>
    [Fact]
    public void ASelfReferencingBaseChainTerminates()
    {
        var (selfWordDoc, selfTable) = StyleSheetOnly((Sti: 46, IstdBase: 0));
        Assert.Null(DocPapx.StyleSheet.Build(selfWordDoc, selfTable, 0).HeadingLevel(0));

        // A two-style cycle: neither is a heading and neither terminates the chain on its own.
        var (cycleWordDoc, cycleTable) = StyleSheetOnly((Sti: 46, IstdBase: 1), (Sti: 47, IstdBase: 0));
        Assert.Null(DocPapx.StyleSheet.Build(cycleWordDoc, cycleTable, 0).HeadingLevel(0));
    }

    // ── the sprm walk, in isolation ───────────────────────────────────────────

    /// <summary>
    /// Operand length comes from a sprm's <c>spra</c> field. Getting one wrong desynchronises the
    /// whole walk, since each sprm's length decides where the next one starts.
    /// </summary>
    [Theory]
    [InlineData(0x260A, 1)]  // sprmPIlvl, spra 1
    [InlineData(0x460B, 2)]  // sprmPIlfo, spra 2
    [InlineData(0x6000, 4)]  // spra 3
    [InlineData(0xE000, 3)]  // spra 7
    public void SprmOperandLengthComesFromSpra(int sprm, int expected) =>
        Assert.Equal(expected, DocPapx.SprmOperandLen((ushort)sprm));

    /// <summary>spra 6 is variable-length, so the caller reads the count from the operand.</summary>
    [Fact]
    public void AVariableLengthSprmHasNoFixedOperandLength() =>
        Assert.Null(DocPapx.SprmOperandLen(0xC000));

    /// <summary>A grpprl carrying other paragraph properties is not a list binding.</summary>
    [Fact]
    public void AGrpprlWithoutIlfoIsNotAListParagraph()
    {
        // sprmPJc (0x2403), spra 1: a paragraph property that is not a list binding.
        Assert.Null(DocPapx.ListBindingFromGrpprl(new byte[] { 0x03, 0x24, 0x01 }));
    }

    /// <summary><c>ilfo == 0</c> is Word's encoding for "no list", not a binding to list 0.</summary>
    [Fact]
    public void IlfoZeroMeansNotInAListRatherThanListZero() =>
        Assert.Null(DocPapx.ListBindingFromGrpprl(new byte[] { 0x0B, 0x46, 0x00, 0x00 }));

    /// <summary>Depth and membership are read together, in either order.</summary>
    [Fact]
    public void IlfoAndIlvlAreReadTogether()
    {
        var binding = DocPapx.ListBindingFromGrpprl(new byte[]
        {
            0x0A, 0x26, 0x02,       // sprmPIlvl = 2
            0x0B, 0x46, 0x09, 0x00, // sprmPIlfo = 9
        });

        Assert.Equal(new DocPapx.ListBinding(9, 2), binding);
    }

    /// <summary>An absent <c>sprmPIlvl</c> means level 0, not "no binding".</summary>
    [Fact]
    public void IlvlDefaultsToZeroWhenOnlyIlfoIsPresent() =>
        Assert.Equal(
            new DocPapx.ListBinding(3, 0),
            DocPapx.ListBindingFromGrpprl(new byte[] { 0x0B, 0x46, 0x03, 0x00 }));
}
