// Ported from Rust `crates/xberg/src/extractors/archive.rs` (`TarExtractor`).

using XRay.Core;
using XRay.Internal.Archive;
using XRay.Types;

namespace XRay.Extractors;

/// <summary>Extracts file lists and text content from TAR archives.</summary>
public sealed class TarExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/x-tar",
        "application/tar",
        "application/x-gtar",
        "application/x-ustar",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var read = TarReader.Read(content.ToArray());
        return ArchiveDocument.Build(read, mimeType, config);
    }
}
