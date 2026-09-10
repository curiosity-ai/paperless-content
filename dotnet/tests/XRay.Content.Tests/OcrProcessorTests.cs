using XRay.Content.Core;
using XRay.Content.Core.Ocr;
using XRay.Content.Types;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// The optional OCR pass: which mode recognises what, where the text lands, and what happens
/// when the recognizer cannot run.
/// </summary>
/// <remarks>
/// Driven through a fake recognizer rather than PaddleOCR. The shipped one needs a multi-gigabyte
/// checkpoint, so every assertion here would otherwise be unrunnable — or, worse, silently
/// skipped, which is how a wrong expectation survived an entire sync in this suite once already.
/// What is under test is the flow: gating, placement, budgets and failure handling. That the
/// recognizer recognises is PaddleOCR's own business.
/// </remarks>
public sealed class OcrProcessorTests
{
    private const string PdfMime = "application/pdf";
    private const string PngMime = "image/png";

    /// <summary>A recognizer that returns a fixed answer and counts what it was asked.</summary>
    private sealed class FakeEngine : IOcrEngine
    {
        private readonly Func<int, OcrImageResult> _answer;
        public FakeEngine(Func<int, OcrImageResult>? answer = null) =>
            _answer = answer ?? (_ => new OcrImageResult("recognised text", 1));

        public List<int> Calls { get; } = new();
        public bool Disposed { get; private set; }

        public OcrImageResult Recognize(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken = default)
        {
            Calls.Add(imageBytes.Length);
            return _answer(Calls.Count - 1);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A recognizer that cannot run at all, as a missing checkpoint would be.</summary>
    private sealed class UnavailableEngine : IOcrEngine
    {
        public int Calls { get; private set; }
        public OcrImageResult Recognize(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new OcrUnavailableException("no checkpoint at '/nowhere'");
        }
        public void Dispose() { }
    }

    /// <summary>A document carrying one image, referenced by an image element between two
    /// paragraphs — the shape that makes inline placement observable.</summary>
    private static InternalDocument DocumentWithImage(int imageBytes = 64 * 64 * 3)
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "before", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Image(0), "", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "after", 0));
        doc.Images.Add(new ExtractedImage
        {
            ImageIndex = 0,
            Format = "png",
            Data = new byte[imageBytes],
            Width = 200,
            Height = 200,
        });
        return doc;
    }

    /// <summary>A PDF whose metadata flags the given pages as scans.</summary>
    private static InternalDocument ScannedPdf(params uint[] pages)
    {
        var doc = new InternalDocument("pdf") { MimeType = PdfMime };
        doc.Metadata.Format = new FormatMetadata
        {
            FormatType = "pdf",
            Payload = new PdfMetadata { ScannedPages = pages.ToList(), ScannedConfidence = 0.9f },
        };
        return doc;
    }

    private static List<string> OcrTexts(InternalDocument doc) =>
        doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.OcrText).Select(e => e.Text).ToList();

    // ── Disabled ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The default does nothing, and never even builds a recognizer.
    /// </summary>
    /// <remarks>
    /// The scanned PDF is the case that matters. An ordinary image document is also left alone by
    /// the later "is there any work?" check, so testing only that would pass even with the mode
    /// gate gone — and a document Disabled was meant to protect would be recognised anyway.
    /// </remarks>
    [Fact]
    public void DisabledRecognisesNothingAndLoadsNoModel()
    {
        foreach (var options in new OcrOptions?[] { null, new OcrOptions { Mode = OcrMode.Disabled } })
        {
            foreach (var (doc, bytes, mime) in new (InternalDocument, byte[], string)[]
                     {
                         (DocumentWithImage(), Array.Empty<byte>(), PngMime),
                         (ScannedPdf(1, 2), new byte[] { 1, 2, 3 }, PdfMime),
                     })
            {
                var engine = new FakeEngine();

                OcrProcessor.Process(doc, bytes, mime,
                    new ExtractionConfig { Ocr = options }, _ => engine);

                Assert.Empty(engine.Calls);
                Assert.Empty(OcrTexts(doc));
                Assert.Empty(doc.ProcessingWarnings);
                Assert.False(doc.Metadata.OcrUsed);
            }
        }
    }

    // ── AllImages ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An image's text is inserted immediately after the element that references it, so it reads
    /// inline rather than being appended to the document.
    /// </summary>
    [Fact]
    public void AllImagesInlinesTheTextAtTheImagesPosition()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        var kinds = doc.Elements.Select(e => e.Kind.Tag).ToList();
        Assert.Equal(
            new[]
            {
                ElementKindTag.Paragraph, ElementKindTag.Image,
                ElementKindTag.OcrText, ElementKindTag.Paragraph,
            },
            kinds);
        Assert.Equal("recognised text", doc.Elements[2].Text);
        Assert.True(doc.Metadata.OcrUsed);
    }

    /// <summary>Rendering the document puts the recognised text between the surrounding
    /// paragraphs, which is what "inline" has to mean for a consumer.</summary>
    [Fact]
    public void TheInlinedTextRendersBetweenItsNeighbours()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        string content = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain).Content;

        Assert.Matches(@"before[\s\S]*recognised text[\s\S]*after", content);
    }

    /// <summary>
    /// Two referenced images each get their own text, each after its own image — the case that
    /// catches insertion order, since inserting the first one front-to-back would shift the
    /// second's position and land its text in the wrong place.
    /// </summary>
    [Fact]
    public void EachImagesTextFollowsItsOwnImage()
    {
        var doc = new InternalDocument("docx") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Image(0), "", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "between", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Image(1), "", 0));
        for (int i = 0; i < 2; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(call => new OcrImageResult($"text {call}", 1)));

        Assert.Equal(
            new[] { "", "text 0", "between", "", "text 1" },
            doc.Elements.Select(e => e.Text).ToList());
    }

    /// <summary>
    /// An image no element references — several extractors collect images separately from the
    /// text — still contributes its text, appended rather than dropped.
    /// </summary>
    [Fact]
    public void AnUnreferencedImagesTextIsAppendedRatherThanDropped()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "body", 0));
        doc.Images.Add(new ExtractedImage { ImageIndex = 0, Data = new byte[4096], Width = 200, Height = 200 });

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        Assert.Equal(new[] { "recognised text" }, OcrTexts(doc));
    }

    /// <summary>An image too small to hold text is not worth recognising: icons, bullets, rules
    /// and spacer GIFs are images too.</summary>
    [Fact]
    public void AnImageBelowTheSizeFloorIsSkipped()
    {
        var doc = DocumentWithImage();
        doc.Images[0].Width = 16;
        doc.Images[0].Height = 16;
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } }, _ => engine);

        Assert.Empty(engine.Calls);
        Assert.Empty(OcrTexts(doc));
    }

    /// <summary>An image of unknown size is recognised rather than skipped — several extractors
    /// record no dimensions, and dropping those would silently disable the mode for them.</summary>
    [Fact]
    public void AnImageOfUnknownSizeIsStillRecognised()
    {
        var doc = DocumentWithImage();
        doc.Images[0].Width = null;
        doc.Images[0].Height = null;

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        Assert.Single(OcrTexts(doc));
    }

    /// <summary>The per-document ceiling bounds the work a figure-heavy document can demand.</summary>
    [Fact]
    public void MaxImagesBoundsTheWork()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        for (int i = 0; i < 10; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages, MaxImages = 3 } },
            _ => engine);

        Assert.Equal(3, engine.Calls.Count);
    }

    /// <summary>An image that holds no text is a successful result, not a failure: no element,
    /// no warning, and OCR is not claimed to have been used.</summary>
    [Fact]
    public void AnImageWithNoTextAddsNothing()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => OcrImageResult.Empty));

        Assert.Empty(OcrTexts(doc));
        Assert.Empty(doc.ProcessingWarnings);
        Assert.False(doc.Metadata.OcrUsed);
    }

    // ── ScanOnly ──────────────────────────────────────────────────────────────

    /// <summary>ScanOnly ignores ordinary images: that is the whole distinction from AllImages.
    /// </summary>
    [Fact]
    public void ScanOnlyDoesNotRecogniseOrdinaryImages()
    {
        var doc = DocumentWithImage();
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.ScanOnly } }, _ => engine);

        Assert.Empty(engine.Calls);
        Assert.Empty(OcrTexts(doc));
    }

    /// <summary>A PDF with no page flagged as a scan is left alone, and no model is loaded.</summary>
    [Fact]
    public void APdfWithNoScannedPageLoadsNoModel()
    {
        var doc = ScannedPdf();
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, new byte[] { 1, 2, 3 }, PdfMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.ScanOnly } }, _ => engine);

        Assert.Empty(engine.Calls);
        Assert.Empty(doc.ProcessingWarnings);
    }

    /// <summary>
    /// A flagged page that cannot be rasterised — here because the bytes are not a PDF at all —
    /// warns and leaves the document intact rather than failing the extraction.
    /// </summary>
    [Fact]
    public void APageThatCannotBeRasterisedWarnsAndKeepsTheDocument()
    {
        var doc = ScannedPdf(1);
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "native text", 0));

        OcrProcessor.Process(doc, new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, PdfMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.ScanOnly } },
            _ => new FakeEngine());

        Assert.Contains(doc.ProcessingWarnings, w => w.Source == "ocr");
        Assert.Contains(doc.Elements, e => e.Text == "native text");
        Assert.Empty(OcrTexts(doc));
    }

    // ── failure handling ──────────────────────────────────────────────────────

    /// <summary>
    /// A missing checkpoint warns once, naming what was looked for, and the document keeps its
    /// native text. It also stops the flow asking again for every remaining image.
    /// </summary>
    [Fact]
    public void AMissingModelWarnsOnceAndKeepsNativeText()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "native text", 0));
        for (int i = 0; i < 5; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });
        var engine = new UnavailableEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } }, _ => engine);

        var warning = Assert.Single(doc.ProcessingWarnings);
        Assert.Equal("ocr", warning.Source);
        Assert.Contains("/nowhere", warning.Message, StringComparison.Ordinal);
        Assert.Equal(1, engine.Calls);
        Assert.Contains(doc.Elements, e => e.Text == "native text");
        Assert.False(doc.Metadata.OcrUsed);
    }

    /// <summary>One image failing does not stop the others: the rest of the document is still
    /// recognised, and the failure is reported.</summary>
    [Fact]
    public void OneFailedImageDoesNotStopTheRest()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        for (int i = 0; i < 3; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(call => call == 1
                ? throw new InvalidOperationException("decoder blew up")
                : new OcrImageResult($"text {call}", 1)));

        Assert.Equal(new[] { "text 0", "text 2" }, OcrTexts(doc));
        Assert.Contains(doc.ProcessingWarnings, w => w.Message.Contains("decoder blew up", StringComparison.Ordinal));
    }

    /// <summary>The recognizer is disposed even when it was never usable — weights are large
    /// enough that leaking one matters.</summary>
    [Fact]
    public void TheEngineIsDisposed()
    {
        var doc = DocumentWithImage();
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } }, _ => engine);

        Assert.True(engine.Disposed);
    }

    /// <summary>
    /// When OCR contributed, the document says so — and says "mixed" rather than "ocr", because
    /// the native text is still there and a consumer treating the whole document as machine-read
    /// would be wrong about most of it.
    /// </summary>
    [Fact]
    public void AContributingPassIsRecordedAsMixed()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        Assert.True(doc.Metadata.OcrUsed);
        Assert.Equal("mixed", doc.Metadata.Additional["extraction_method"].GetString());

        // And it survives to the public result, which is where a consumer reads it.
        var result = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain);
        Assert.Equal(ExtractionMethod.Mixed, result.ExtractionMethod);
    }

    // ── tables the recognizer reports as HTML ─────────────────────────────────

    /// <summary>
    /// What PaddleOCR-VL produces for a table region: <c>OtslTable.ToHtml</c>'s output shape —
    /// <c>&lt;td&gt;</c> throughout, no header row, cell text HTML-encoded.
    /// </summary>
    private const string RecognizedTable =
        "<table><tr><td>Stock</td><td>Last</td></tr>"
        + "<tr><td>ABX Air n</td><td>7.52</td></tr></table>";

    /// <summary>
    /// A table the recognizer reported as markup becomes a table of the document's own, not a
    /// text element holding HTML — which is what lets each output format write it in its own
    /// notation.
    /// </summary>
    [Fact]
    public void ARecognizedHtmlTableBecomesATableElement()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(RecognizedTable, 1)));

        Assert.Equal(
            new[]
            {
                ElementKindTag.Paragraph, ElementKindTag.Image,
                ElementKindTag.Table, ElementKindTag.Paragraph,
            },
            doc.Elements.Select(e => e.Kind.Tag).ToList());

        var table = Assert.Single(doc.Tables);
        Assert.Equal(
            new[] { new[] { "Stock", "Last" }, new[] { "ABX Air n", "7.52" } },
            table.Cells.Select(r => r.ToArray()).ToArray());
    }

    /// <summary>
    /// The point of the exercise: Markdown gets a pipe table, with no HTML left in it.
    /// </summary>
    [Fact]
    public void TheRecoveredTableRendersAsAMarkdownTable()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(RecognizedTable, 1)));

        string content = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Markdown).Content;

        Assert.Contains("| Stock | Last |", content);
        Assert.Contains("| ABX Air n | 7.52 |", content);
        Assert.DoesNotContain("<table", content);
        Assert.DoesNotContain("<td", content);
    }

    /// <summary>
    /// The same table under the other formats, which is the argument for recovering it into the
    /// model rather than rewriting the string: HTML keeps a real table, and plain text carries
    /// the cells without anyone's markup.
    /// </summary>
    [Fact]
    public void TheRecoveredTableRendersInEachFormatsOwnNotation()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(RecognizedTable, 1)));

        string html = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Html).Content;
        Assert.Contains("<table", html);
        Assert.Contains("ABX Air n", html);

        string plain = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain).Content;
        Assert.Contains("ABX Air n", plain);
        Assert.DoesNotContain("<td", plain);
    }

    /// <summary>
    /// The text around a table keeps its place, so a page that is a heading, a table and a
    /// footnote still reads in that order.
    /// </summary>
    [Fact]
    public void TextAroundATableKeepsItsOrder()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(
                $"Nasdaq & AMEX\n\n{RecognizedTable}\n\nStocks in bold rose or fell 5%", 1)));

        Assert.Equal(
            new[]
            {
                ElementKindTag.Paragraph, ElementKindTag.Image,
                ElementKindTag.OcrText, ElementKindTag.Table, ElementKindTag.OcrText,
                ElementKindTag.Paragraph,
            },
            doc.Elements.Select(e => e.Kind.Tag).ToList());

        Assert.Equal("Nasdaq & AMEX", doc.Elements[2].Text);
        Assert.Equal("Stocks in bold rose or fell 5%", doc.Elements[4].Text);
    }

    /// <summary>
    /// A cell holds the text the camera saw: entities decoded, because <c>OtslTable</c> encodes
    /// what it writes, and nothing escaped, because the grid is what the plain and HTML
    /// renderers read as well — Markdown's escaping in it would show up in both.
    /// </summary>
    [Fact]
    public void ACellHoldsPlainDecodedText()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(
                "<table><tr><td>AT&amp;T | ACMoore*</td><td>39.20</td></tr></table>", 1)));

        Assert.Equal("AT&T | ACMoore*", Assert.Single(doc.Tables).Cells[0][0]);

        string plain = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain).Content;
        Assert.Contains("AT&T | ACMoore*", plain);

        // Markdown, and only Markdown, escapes the pipe — once, so a reader gets one character
        // back rather than a backslash to discount.
        string markdown = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Markdown).Content;
        Assert.Contains(@"| AT\&T \| ACMoore* | 39.20 |", markdown);
    }

    /// <summary>
    /// A merged cell keeps the rest of the row lined up with its headers: the spanning cell's
    /// text sits at its origin and the columns it covers stay empty, which is the shared
    /// <c>GridFlatten</c> placement every other format's tables get.
    /// </summary>
    [Fact]
    public void AMergedCellKeepsTheGridAligned()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(
                "<table><tr><td colspan=\"2\">52-week</td><td>Stock</td></tr>"
                + "<tr><td>9.19</td><td>6.89</td><td>ABX Air n</td></tr></table>", 1)));

        Assert.Equal(
            new[]
            {
                new[] { "52-week", "", "Stock" },
                new[] { "9.19", "6.89", "ABX Air n" },
            },
            Assert.Single(doc.Tables).Cells.Select(r => r.ToArray()).ToArray());
    }

    /// <summary>
    /// Markup that yields no grid — an empty table here — is left in the text verbatim rather
    /// than becoming a table invented from a failed read. Markup a consumer can still parse
    /// beats cells that were never recognised.
    /// </summary>
    [Fact]
    public void MarkupThatYieldsNoGridIsLeftAsText()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(
                "<table><tr><td></td></tr></table>", 1)));

        Assert.Empty(doc.Tables);
        Assert.Equal(
            new[] { "<table><tr><td></td></tr></table>" }, OcrTexts(doc));
    }

    /// <summary>
    /// A recovered table takes the page of the image it came from, on the element and on the
    /// table itself — the page is what relates a recognition to where it was found.
    /// </summary>
    [Fact]
    public void ARecoveredTableCarriesItsImagesPage()
    {
        var doc = DocumentWithImage();
        doc.Elements[1].Page = 4;

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => new OcrImageResult(RecognizedTable, 1)));

        Assert.Equal(4u, doc.Elements[2].Page);
        Assert.Equal(4u, Assert.Single(doc.Tables).PageNumber);
    }
}
