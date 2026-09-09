namespace XRay.Core.Ocr;

/// <summary>What recognition made of one image.</summary>
/// <param name="Text">
/// The recognised text, already in reading order. Empty when the image holds no text — which is
/// a successful result, not a failure.
/// </param>
/// <param name="Blocks">How many regions the recognizer found, for the warning text and tests.</param>
public readonly record struct OcrImageResult(string Text, int Blocks)
{
    /// <summary>An image that yielded nothing.</summary>
    public static OcrImageResult Empty { get; } = new("", 0);

    /// <summary>Whether there is anything worth putting in the document.</summary>
    public bool HasText => Text.Length > 0;
}

/// <summary>
/// Recognises text in an encoded image.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists for two reasons, both practical. The shipped recognizer needs a
/// multi-gigabyte checkpoint on disk, so every test of the surrounding flow — which mode fires
/// on what, where the text lands, what happens when a model is missing — would otherwise be
/// untestable or, worse, silently skipped. And it keeps the PaddleOCR dependency behind one
/// type, so the rest of the port neither references it nor loads it when OCR is off.
/// </para>
/// <para>
/// An implementation is expected to be expensive to construct (weights) and cheap to call, so
/// callers build one per document at most, and only once a mode has decided there is work.
/// </para>
/// </remarks>
public interface IOcrEngine : IDisposable
{
    /// <summary>
    /// Recognise one encoded image (PNG, JPEG, or anything else the implementation decodes).
    /// </summary>
    /// <param name="imageBytes">The encoded image.</param>
    /// <param name="cancellationToken">Cancels a recognition that has outrun its budget.</param>
    /// <returns>What the image says, or <see cref="OcrImageResult.Empty"/> when it says nothing.</returns>
    /// <exception cref="OcrUnavailableException">
    /// The recognizer cannot run at all — no checkpoint, or an unreadable one. Distinct from an
    /// image that simply holds no text, because the caller reports the two differently.
    /// </exception>
    OcrImageResult Recognize(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// The recognizer cannot run: its model checkpoint is absent or unreadable.
/// </summary>
/// <remarks>
/// Its own type because the caller's response is specific — record one warning naming the
/// directory it looked in, stop trying for the rest of the document, and return the native text
/// untouched. A document is not a failure for lacking OCR.
/// </remarks>
public sealed class OcrUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OcrUnavailableException(string message) : base(message) { }

    /// <summary>Creates the exception, preserving what actually went wrong underneath.</summary>
    public OcrUnavailableException(string message, Exception inner) : base(message, inner) { }
}
