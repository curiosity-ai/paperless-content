using System.Text;
using Paperless.Content.Core;
using Paperless.Content.Extractors;
using Xunit;

namespace Paperless.Content.Tests;

public class MimeTests
{
    [Theory]
    [InlineData("file.txt", "text/plain")]
    [InlineData("a/b/doc.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("index.HTML", "text/html")]
    [InlineData("data.csv", "text/csv")]
    [InlineData("config.json", "application/json")]
    public void DetectFromExtension(string path, string expected)
    {
        Assert.Equal(expected, Mime.DetectMimeType(path, checkExists: false));
    }

    [Fact]
    public void UnknownExtensionReturnsNull()
    {
        Assert.Null(Mime.DetectMimeType("mystery.zzz", checkExists: false));
    }

    [Fact]
    public void DetectPdfFromBytes()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n...");
        Assert.Equal("application/pdf", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void DetectPngFromMagic()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Equal("image/png", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void DetectJsonFromText()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"key\": \"value\"}");
        Assert.Equal("application/json", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void DetectPlainTextFromText()
    {
        var bytes = Encoding.UTF8.GetBytes("just some words here");
        Assert.Equal("text/plain", Mime.DetectMimeTypeFromBytes(bytes));
    }

    [Fact]
    public void GetExtensionsForMimeIncludesKnown()
    {
        var exts = Mime.GetExtensionsForMime("image/jpeg");
        Assert.Contains("jpg", exts);
        Assert.Contains("jpeg", exts);
    }

    // ── extension vs content ───────────────────────────────────────────────────

    /// <summary>
    /// An extension is a claim; a recognisable signature is evidence. The corpus carries markup
    /// and DocTags streams saved as `.txt`, which routed to the plain-text extractor and came out
    /// as their own raw source.
    /// </summary>
    [Fact]
    public void ContentOverrulesTheExtensionWhenTheyDisagree()
    {
        var markup = Encoding.UTF8.GetBytes("<doctag><text>Body.</text></doctag>");
        Assert.Equal("application/xml", Mime.ResolveWithContent("text/plain", markup));

        var pdf = Encoding.UTF8.GetBytes("%PDF-1.7\nrest");
        Assert.Equal("application/pdf", Mime.ResolveWithContent("text/plain", pdf));
    }

    [Fact]
    public void AgreementLeavesTheExtensionAlone()
    {
        var text = Encoding.UTF8.GetBytes("just some words here");
        Assert.Equal("text/plain", Mime.ResolveWithContent("text/plain", text));
    }

    /// <summary>
    /// Plain text is not a signature — it is the absence of one, and must never displace a more
    /// specific extension.
    /// </summary>
    [Fact]
    public void PlainTextContentNeverDisplacesAMoreSpecificExtension()
    {
        var prose = Encoding.UTF8.GetBytes("# Heading\n\nSome prose.\n");
        Assert.Equal("text/markdown", Mime.ResolveWithContent("text/markdown", prose));
    }

    /// <summary>
    /// A generic signature is strictly less informative than the extension rather than in
    /// conflict with it: XML cannot tell FictionBook from DocBook, and JSON cannot tell a
    /// notebook from line-delimited JSON.
    /// </summary>
    [Fact]
    public void GenericSignaturesDoNotDisplaceASpecificVocabulary()
    {
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><FictionBook/>");
        Assert.Equal("application/x-fictionbook+xml", Mime.ResolveWithContent("application/x-fictionbook+xml", xml));

        var json = Encoding.UTF8.GetBytes("{\"cells\": []}");
        Assert.Equal("application/x-ipynb+json", Mime.ResolveWithContent("application/x-ipynb+json", json));
    }

    /// <summary>
    /// A container signature identifies the wrapper, not the format inside it, so it must never
    /// displace an extension that names one. Every OLE compound file looks alike without reading
    /// the root CLSID, and a ZIP that is not one of the recognised Office layouts is just a ZIP.
    /// </summary>
    [Theory]
    [InlineData("application/vnd.ms-outlook")]
    [InlineData("application/haansofthwp")]
    [InlineData("application/vnd.ms-excel")]
    public void CompoundFileSignaturesDoNotDisplaceASpecificExtension(string extensionMime)
    {
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        Assert.Equal(extensionMime, Mime.ResolveWithContent(extensionMime, ole));
    }

    [Fact]
    public void BareZipSignatureDoesNotDisplaceASpecificExtension()
    {
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x0A, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal("application/epub+zip", Mime.ResolveWithContent("application/epub+zip", zip));
    }

    /// <summary>With no extension to go on, content is all there is.</summary>
    [Fact]
    public void WithoutAnExtensionContentDecidesAlone()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Equal("image/png", Mime.ResolveWithContent(null, png));
    }

    /// <summary>
    /// HTML must be recognised before the generic `&lt;` fallback claims every tag. Behind it the
    /// HTML test is unreachable and every HTML file types as XML.
    /// </summary>
    [Theory]
    [InlineData("<!doctype html>\n<html><body>Hi</body></html>")]
    [InlineData("<!DOCTYPE HTML>\n<HTML></HTML>")]
    [InlineData("<html lang=\"en\"><body>Hi</body></html>")]
    // Bare fragments have only their first element to go on.
    [InlineData("<div class=\"x\">Hi</div>")]
    [InlineData("<table><tr><td>1</td></tr></table>")]
    [InlineData("<p>Hi</p>")]
    public void HtmlIsRecognisedBeforeTheGenericMarkupFallback(string markup)
    {
        Assert.Equal("text/html", Mime.DetectMimeTypeFromBytes(Encoding.UTF8.GetBytes(markup)));
    }

    [Theory]
    // A declaration means XML however HTML-ish what follows looks.
    [InlineData("<?xml version=\"1.0\"?><html></html>")]
    // A namespace prefix that collides with an HTML element name is still XML: the tag does not
    // end at the name.
    [InlineData("<tr:foo>bar</tr:foo>")]
    // An element that is not an HTML one.
    [InlineData("<doctag><text>Body.</text></doctag>")]
    [InlineData("<FictionBook><body/></FictionBook>")]
    public void NonHtmlMarkupStaysXml(string markup)
    {
        Assert.Equal("application/xml", Mime.DetectMimeTypeFromBytes(Encoding.UTF8.GetBytes(markup)));
    }

    /// <summary>An empty file has nothing to contradict the extension with.</summary>
    [Fact]
    public void EmptyContentLeavesTheExtensionAlone()
    {
        Assert.Equal("text/markdown", Mime.ResolveWithContent("text/markdown", ReadOnlySpan<byte>.Empty));
    }

    /// <summary>
    /// A page whose first line is a comment — a `Last-Modified` note above the DOCTYPE, as three
    /// of the regression fixtures have — is still HTML. Without skipping it the DOCTYPE is never
    /// seen and the file reaches the XML extractor, which renders it as an indented tag outline.
    /// </summary>
    [Theory]
    [InlineData("<!-- Last-Modified: Mon, 22 Sep 2008 -->\n<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\">\n<html><body><p>hi</p></body></html>")]
    [InlineData("<!-- one --><!-- two -->\n<HTML>\n<HEAD><TITLE>t</TITLE></HEAD><BODY>x</BODY>")]
    public void CommentPrefixedMarkupIsStillHtml(string markup)
    {
        Assert.Equal("text/html", Mime.DetectMimeTypeFromBytes(Encoding.UTF8.GetBytes(markup)));
    }

    /// <summary>
    /// The WHATWG table upstream reaches through <c>infer</c> answers on the opening alone:
    /// <c>&lt;!--</c> followed by a space is HTML whether or not the comment is ever closed. The
    /// corpus depends on it — <c>ground_truth/pdf/160428551.txt</c> opens with an unterminated
    /// <c>&lt;!-- … --</c> and upstream extracts it as HTML, not as a tag outline.
    /// </summary>
    [Fact]
    public void AnUnterminatedCommentStillOpensHtml()
    {
        Assert.Equal("text/html", Mime.DetectMimeTypeFromBytes(
            Encoding.UTF8.GetBytes("<!-- never closed\n<book><title>t</title></book>")));
    }

    /// <summary>
    /// The same table is strict about the delimiter: only a space or <c>&gt;</c> ends the
    /// opening, so markup that merely begins with those four characters is not HTML.
    /// </summary>
    [Fact]
    public void AnOpeningWithoutItsDelimiterIsNotHtml()
    {
        Assert.Equal("application/xml", Mime.DetectMimeTypeFromBytes(
            Encoding.UTF8.GetBytes("<!--never closed\n<book><title>t</title></book>")));
    }

    /// <summary>
    /// A corrupted download can staple an ISP error page in front of a real document —
    /// `test_documents/pdf/medium.pdf` opens with 364 bytes of DNS-hijack HTML and then
    /// `%PDF-1.4`. The opening bytes read as markup, but the file as a whole does not decode as
    /// text, and treating it as HTML hands the PDF's raw bytes to the wrong extractor.
    /// </summary>
    [Fact]
    public void AnErrorPageStapledToABinaryDocumentDoesNotMakeItHtml()
    {
        byte[] preamble = System.Text.Encoding.ASCII.GetBytes(
            "<html><head><meta http-equiv=\"refresh\" content=\"0;url=http://example.invalid/\"/>" +
            "</head><body></body></html>");
        byte[] pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\r%\u00e2\u00e3\u00cf\u00d3\r\n114 0 obj\n");
        // A byte that cannot start a UTF-8 sequence, as a real PDF's binary comment carries.
        var bytes = new List<byte>(preamble);
        bytes.AddRange(pdf);
        bytes.AddRange(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 });

        Assert.Equal("application/pdf", Mime.ResolveWithContent("application/pdf", bytes.ToArray()));
    }

    /// <summary>A page that really is HTML is still HTML, whatever it goes on to contain.</summary>
    [Fact]
    public void AnHtmlDocumentStillOverridesAMisleadingExtension()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
            "<html><head><title>t</title></head><body><p>hello</p></body></html>");

        Assert.Equal("text/html", Mime.ResolveWithContent("text/plain", bytes));
    }

    // ── legacy OLE2 typing by root CLSID (xberg-io/xberg#1590) ────────────────

    /// <summary>
    /// A compound file cannot be typed from its magic bytes alone: <c>.doc</c>, <c>.xls</c> and
    /// <c>.ppt</c> share the container, and only the root storage's CLSID tells them apart. Before
    /// upstream's <c>fix(pdf,mime): … type OLE2 files by path</c> the port answered
    /// <c>application/msword</c> for all three.
    /// </summary>
    [Theory]
    [InlineData("00020906-0000-0000-c000-000000000046", "application/msword")]
    [InlineData("00020810-0000-0000-c000-000000000046", "application/vnd.ms-excel")]
    [InlineData("00020820-0000-0000-c000-000000000046", "application/vnd.ms-excel")]
    [InlineData("64818d10-4f9b-11cf-86ea-00aa00b929e8", "application/vnd.ms-powerpoint")]
    public void ALegacyOleDocumentIsTypedByItsRootClsid(string clsid, string expected)
    {
        // Padded past the 4 KiB sniff window, which is the size a real Office document has and
        // the reason the header alone can never settle this: the FAT chain that locates the root
        // directory entry references sectors a truncated prefix does not contain.
        byte[] content = CfbBuilder.Build(Guid.Parse(clsid), ("Padding", new byte[8192]));
        Assert.True(content.Length > 4096);

        Assert.Equal(expected, Mime.DetectMimeTypeFromBytes(content));
    }

    /// <summary>The CLSID is confident enough to overrule a wrong extension, as any other
    /// content-based finding is.</summary>
    [Fact]
    public void AWorkbookNamedDocIsStillAWorkbook()
    {
        byte[] content = CfbBuilder.Build(
            Guid.Parse("00020810-0000-0000-c000-000000000046"), ("Padding", new byte[8192]));

        Assert.Equal("application/vnd.ms-excel", Mime.ResolveWithContent("application/msword", content));
    }

    /// <summary>A container declaring a CLSID this port does not recognise stays ambiguous, so
    /// the extension keeps its say — an <c>.msg</c> or <c>.hwp</c> must not be renamed to Word.
    /// </summary>
    [Fact]
    public void AnUnrecognisedClsidLeavesTheExtensionInCharge()
    {
        byte[] content = CfbBuilder.Build(
            Guid.Parse("0006f020-0000-0000-c000-000000000046"), ("Padding", new byte[8192]));

        Assert.Equal("application/vnd.ms-outlook", Mime.ResolveWithContent("application/vnd.ms-outlook", content));
    }

    // ── geospatial formats (upstream feat(formats): add KML and GeoJSON support) ──

    /// <summary>
    /// Each keeps its own MIME so a consumer can tell what it is, while routing to the extractor
    /// for the syntax it is written in. The content sniff sees generic XML or JSON, so the
    /// specific extension has to win.
    /// </summary>
    [Theory]
    [InlineData("map.kml",
        "<?xml version=\"1.0\"?><kml xmlns=\"http://www.opengis.net/kml/2.2\"><Placemark><name>Berlin</name></Placemark></kml>",
        "application/vnd.google-earth.kml+xml")]
    [InlineData("point.geojson",
        "{\"type\":\"Point\",\"coordinates\":[13.4,52.5]}",
        "application/geo+json")]
    public void AGeospatialFileKeepsItsOwnMimeType(string fileName, string content, string expected)
    {
        string? fromExtension = Mime.DetectMimeType(fileName, checkExists: false);

        Assert.Equal(expected, fromExtension);
        Assert.Equal(expected, Mime.ResolveWithContent(fromExtension, Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    /// Upstream <c>feat(diagram): extract flat ODF drawings (.fodg)</c>. Flat ODF drawings
    /// advertised no extractor at all. A flat document carries the packaged MIME type inside
    /// itself, as the root element's <c>office:mimetype</c>, so content detection reads that
    /// attribute rather than sniffing a ZIP container — and identifies one even without the
    /// extension.
    /// </summary>
    [Fact]
    public void AFlatOdfDrawingIsDetectedFromItsOwnMimetypeAttribute()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<office:document xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
            "office:mimetype=\"application/vnd.oasis.opendocument.graphics\">" +
            "<office:body/></office:document>");

        Assert.Equal(Mime.OdgFlatMimeType, Mime.DetectMimeType("diagram.fodg", checkExists: false));
        Assert.Equal(Mime.OdgFlatMimeType, Mime.DetectMimeTypeFromBytes(content));
        Assert.Contains(Mime.OdgFlatMimeType, new XmlExtractor().SupportedMimeTypes);
    }

    /// <summary>The attribute is trusted only on a real <c>office:document</c> root in the ODF
    /// office namespace — a stylesheet that merely mentions the type is not a drawing.</summary>
    [Fact]
    public void AnUnrelatedRootCarryingTheAttributeIsNotAFlatDrawing()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "<wrapper office:mimetype=\"application/vnd.oasis.opendocument.graphics\"/>");

        Assert.NotEqual(Mime.OdgFlatMimeType, Mime.DetectMimeTypeFromBytes(content));
    }

    /// <summary>
    /// Upstream <c>fix(mime): reject unsupported vocabulary MIME</c>. A more specific vocabulary
    /// only outranks the generic syntax it is written in when this port can actually extract it:
    /// <c>.atom</c> and <c>.gltf</c> are XML and JSON vocabularies nothing here handles, so those
    /// files are better served as the XML or JSON the content says they are than as a type no
    /// extractor claims.
    /// </summary>
    [Theory]
    [InlineData("application/atom+xml", "<?xml version=\"1.0\"?><feed/>", "application/xml")]
    [InlineData("model/gltf+json", "{\"asset\":{\"version\":\"2.0\"}}", "application/json")]
    public void AnUnsupportedVocabularyDoesNotOverruleTheSyntaxItIsWrittenIn(
        string extensionMime, string content, string expected)
    {
        Assert.Equal(expected, Mime.ResolveWithContent(extensionMime, Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>A vocabulary the port does extract still wins, as it must.</summary>
    [Fact]
    public void ASupportedVocabularyStillOverrulesGenericContent()
    {
        Assert.Equal(
            "application/x-fictionbook+xml",
            Mime.ResolveWithContent(
                "application/x-fictionbook+xml",
                Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><FictionBook/>")));
    }

    /// <summary>
    /// Upstream <c>feat(mime): complete format and extension registry</c>. Each of these
    /// extensions names a format an extractor here already claims, and each resolved to nothing:
    /// an <c>.xhtml</c> file never reached the HTML extractor at all, and the HEIF sequence types
    /// the image extractor advertises had no extension pointing at them.
    /// </summary>
    [Theory]
    [InlineData("page.xhtml", "application/xhtml+xml")]
    [InlineData("page.xht", "application/xhtml+xml")]
    [InlineData("notes.dj", "text/x-djot")]
    [InlineData("deck.pps", "application/vnd.ms-powerpoint")]
    [InlineData("book.xltm", "application/vnd.ms-excel.template.macroEnabled.12")]
    [InlineData("addin.xla", "application/vnd.ms-excel")]
    [InlineData("photo.hif", "image/heif")]
    [InlineData("burst.heifs", "image/heif-sequence")]
    [InlineData("burst.heics", "image/heic-sequence")]
    public void AnAliasExtensionResolvesToItsFormat(string fileName, string expected) =>
        Assert.Equal(expected, Mime.DetectMimeType(fileName, checkExists: false));

    /// <summary>
    /// Each alias above has to reach a real extractor, not just resolve to a name. The registry
    /// derives its "supported" set from the extension table itself, so that set cannot answer
    /// this — only the extractor registry can.
    /// </summary>
    [Theory]
    [InlineData("page.xhtml")]
    [InlineData("notes.dj")]
    [InlineData("deck.pps")]
    [InlineData("photo.hif")]
    [InlineData("burst.heifs")]
    [InlineData("burst.heics")]
    public void AnAliasExtensionReachesAnExtractor(string fileName)
    {
        string? mime = Mime.DetectMimeType(fileName, checkExists: false);

        Assert.NotNull(mime);
        Assert.NotNull(Registry.RegisterDefaults().ForMime(mime!));
    }
}
