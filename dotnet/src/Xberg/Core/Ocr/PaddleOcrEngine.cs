using PaddleOcrSharp.Download;
using PaddleOcrSharp.Imaging;
using PaddleOcrSharp.Pipeline;

namespace Xberg.Core.Ocr;

/// <summary>
/// The shipped recognizer: PaddleOCR-VL through the <c>PaddleOCR</c> package.
/// </summary>
/// <remarks>
/// <para>
/// Weights are loaded once, on the first call, and reused for the rest of the document — the
/// checkpoint is gigabytes and loading it per image would dominate everything else. Construction
/// is therefore cheap and deliberately does no I/O, so a mode that turns out to have no work
/// costs nothing.
/// </para>
/// <para>
/// Nothing here downloads a model. <see cref="OcrOptions.ModelDirectory"/> names where the
/// checkpoint already is; absent that, the PaddleOCR cache root is consulted, so a cache warmed
/// out of band works without configuration. A missing checkpoint raises
/// <see cref="OcrUnavailableException"/> rather than fetching gigabytes as a side effect of an
/// extraction call.
/// </para>
/// </remarks>
internal sealed class PaddleOcrEngine : IOcrEngine
{
    private readonly OcrOptions _options;
    private DocumentParser? _parser;
    private bool _loadFailed;
    private string? _loadFailure;
    /// <summary>Whether a layout detector was resolved, which decides whether the recognizer
    /// segments a page or reads it whole. Known only once the parser has been loaded.</summary>
    private bool _hasLayout;

    public PaddleOcrEngine(OcrOptions options) => _options = options;

    /// <summary>
    /// Where the checkpoints are, preferring what the caller configured and falling back to the
    /// PaddleOCR cache root so a pre-warmed cache needs no configuration.
    /// </summary>
    private (string Vl, string? Layout) ResolveDirectories()
    {
        if (_options.ModelDirectory is { Length: > 0 } configured)
            return (configured, _options.LayoutModelDirectory);

        // `ModelDownloader` owns the cache layout; asking it for the directory keeps this in
        // step with the library rather than hard-coding a path shape that could drift.
        using var downloader = new ModelDownloader();
        string vl = downloader.DirectoryFor(ModelCatalog.PaddleOcrVL16);
        string layout = downloader.DirectoryFor(ModelCatalog.PpDocLayoutV3);
        return (vl, _options.LayoutModelDirectory ?? (Directory.Exists(layout) ? layout : null));
    }

    private DocumentParser EnsureParser()
    {
        if (_parser is not null) return _parser;
        // A second attempt would re-pay the whole load cost to fail the same way, and the caller
        // stops asking after the first failure anyway; this makes that cheap if it ever does not.
        if (_loadFailed) throw new OcrUnavailableException(_loadFailure!);

        var (vl, layout) = ResolveDirectories();
        if (!Directory.Exists(vl))
        {
            _loadFailed = true;
            _loadFailure =
                $"the PaddleOCR-VL checkpoint is not at '{vl}'. Set OcrOptions.ModelDirectory to "
                + "where it is, or stage it into the PaddleOCR cache; nothing here downloads it.";
            throw new OcrUnavailableException(_loadFailure);
        }

        try
        {
            _parser = DocumentParser.Load(vl, layout);
            _hasLayout = layout is not null;
            return _parser;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or InvalidDataException or NotSupportedException)
        {
            _loadFailed = true;
            _loadFailure = $"the PaddleOCR-VL checkpoint at '{vl}' could not be loaded: {e.Message}";
            throw new OcrUnavailableException(_loadFailure, e);
        }
    }

    public OcrImageResult Recognize(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length == 0) return OcrImageResult.Empty;
        var parser = EnsureParser();

        RgbImage? page = null;
        try
        {
            page = ImageIO.Load(imageBytes);
        }
        catch (Exception e) when (e is not OcrUnavailableException)
        {
            // An image this port could extract but the decoder cannot read is one image's
            // problem, not the document's: report nothing and let the caller carry on.
            return OcrImageResult.Empty;
        }

        try
        {
            var parsed = parser.Parse(
                page,
                new DocumentParserOptions { UseLayoutDetection = _hasLayout },
                cancellationToken: cancellationToken);

            string text = string.Join(
                "\n\n",
                parsed.Blocks.Select(block => block.Content.Trim()).Where(content => content.Length > 0));

            return new OcrImageResult(text, parsed.Blocks.Count);
        }
        finally
        {
            page.Dispose();
        }
    }

    public void Dispose()
    {
        _parser?.Dispose();
        _parser = null;
    }
}
