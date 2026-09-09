// Ported from Rust `crates/xberg/src/extractors/archive.rs` (`ZipExtractor`).

using XRay.Content.Core;
using XRay.Content.Internal.Archive;
using XRay.Content.Types;

namespace XRay.Content.Extractors;

/// <summary>Extracts file lists and text content from ZIP archives, and recursively
/// extracts each child through the public pipeline into <c>Children</c>.</summary>
public sealed class ZipExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/zip",
        "application/x-zip-compressed",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var read = ZipReader.Read(content.ToArray(), config.SecurityLimits);
        return ArchiveDocument.Build(read, mimeType, config);
    }
}
