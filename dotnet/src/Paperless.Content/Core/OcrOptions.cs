namespace Paperless.Content.Core;

/// <summary>
/// When the optional OCR pass runs.
/// </summary>
/// <remarks>
/// OCR is a deviation from the Rust original's scope for this port — see "Deviation: optional
/// OCR" in <c>dotnet/Claude.md</c>. It is off unless a caller turns it on, and it never replaces
/// native text: a page that yields text without OCR keeps that text.
/// </remarks>
public enum OcrMode
{
    /// <summary>No OCR. The port's default, and its behaviour before OCR existed.</summary>
    Disabled = 0,

    /// <summary>
    /// OCR only the pages a scan detector flags — a PDF page that is an image of text rather
    /// than text. This is the mode that recovers a scanned document without paying to recognise
    /// artwork in documents that already have their text.
    /// </summary>
    ScanOnly = 1,

    /// <summary>
    /// OCR every image the extraction produced, inlining what each one says at the point the
    /// image appears, plus the scanned pages <see cref="ScanOnly"/> would have covered.
    /// </summary>
    AllImages = 2,
}

/// <summary>
/// Settings for the optional OCR pass.
/// </summary>
/// <remarks>
/// The recognizer is PaddleOCR-VL, which needs a model checkpoint on disk. Nothing here
/// downloads it: <see cref="ModelDirectory"/> names where it already is, and OCR reports a
/// processing warning and leaves the document's native text alone when it is not there. An
/// extraction call should not be able to start a multi-gigabyte download as a side effect.
/// </remarks>
public sealed class OcrOptions
{
    /// <summary>When the pass runs. Defaults to <see cref="OcrMode.Disabled"/>.</summary>
    public OcrMode Mode { get; set; } = OcrMode.Disabled;

    /// <summary>
    /// Directory holding the PaddleOCR-VL checkpoint. When null, the recognizer looks in the
    /// PaddleOCR cache root (<c>~/.cache/paddleocr-sharp</c> unless the library is configured
    /// otherwise), so a pre-warmed cache works without naming a path here.
    /// </summary>
    public string? ModelDirectory { get; set; }

    /// <summary>
    /// Directory holding the PP-DocLayoutV3 layout detector. When null the recognizer reads each
    /// image as one region, which is right for a cropped figure and coarse for a full page.
    /// </summary>
    public string? LayoutModelDirectory { get; set; }

    /// <summary>
    /// Resolution, in dots per inch, at which a PDF page is rasterised before recognition.
    /// Higher reads small type more reliably and costs proportionally more pixels.
    /// </summary>
    public int Dpi { get; set; } = 200;

    /// <summary>
    /// Most images to recognise in one document. A document carrying hundreds of figures would
    /// otherwise turn one extraction into hours of recognition.
    /// </summary>
    public int MaxImages { get; set; } = 64;

    /// <summary>
    /// Smallest image, in total pixels, worth recognising. Icons, bullets, rules and spacer GIFs
    /// are images too, and they hold no text; recognising them costs time and yields noise.
    /// </summary>
    public int MinImagePixels { get; set; } = 64 * 64;

    /// <summary>
    /// A per-image ceiling on recognition time. Exceeding it records a warning and leaves the
    /// remaining images unrecognised rather than letting one pathological input run unbounded.
    /// </summary>
    public TimeSpan PerImageTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
