using System.IO.Compression;
using System.Text;
using XRay.Core;
using XRay.Extractors;
using Xunit;

namespace XRay.Tests;

/// <summary>
/// Upstream <c>fix(security): bound container reads by declared member size</c>
/// (GHSA-85w9-wqcq-x48r): a ZIP member's uncompressed size comes from the central directory,
/// which the archive's author chooses freely, and nothing decompresses to check it — every
/// archive-level guard reads that declared size and stops there. Upstream's ZIP reader puts no
/// bound on the decompressed side, so a member forging a small declared size while carrying a
/// large deflate stream inflated without limit, and each call site had to add its own bound.
/// </summary>
/// <remarks>
/// This port reads members through <see cref="ZipArchive"/>, which enforces that bound itself:
/// a member's stream ends at the size it declared, whatever the deflate stream would have gone
/// on to produce. There is nothing to fix here, and these tests exist to say so — and to fail
/// loudly if member reading ever moves to a hand-rolled reader that does not carry the same
/// guarantee.
/// </remarks>
public sealed class ZipDeclaredSizeTests
{
    /// <summary>A megabyte of zeroes deflates to almost nothing, so a forged claim of a few
    /// bytes is wildly smaller than what the stream would otherwise yield.</summary>
    private const int PayloadBytes = 1024 * 1024;
    private const uint ForgedSize = 8;
    private const string MemberName = "bomb.txt";

    private static byte[] Zip(params (string Name, byte[] Data)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in parts)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
        return ms.ToArray();
    }

    /// <summary>
    /// Rewrite one member's <em>declared</em> uncompressed size, in both the local file header
    /// and the central directory, leaving the deflate stream and the CRC untouched. The member
    /// reads back cleanly and only its size claim is a lie — precisely the shape an archive-level
    /// guard cannot see, since those read declared sizes and never decompress.
    /// </summary>
    private static byte[] ForgeDeclaredSize(byte[] data, string memberName, uint forgedSize)
    {
        byte[] member = Encoding.ASCII.GetBytes(memberName);
        int patched = 0;

        // Central directory records: name length at +28, uncompressed size at +24, the local
        // header's offset at +42.
        for (int i = 0; i + 46 <= data.Length; i++)
        {
            if (!(data[i] == 'P' && data[i + 1] == 'K' && data[i + 2] == 0x01 && data[i + 3] == 0x02)) continue;
            int nameLen = BitConverter.ToUInt16(data, i + 28);
            if (i + 46 + nameLen > data.Length) continue;
            if (!data.AsSpan(i + 46, nameLen).SequenceEqual(member)) continue;

            BitConverter.TryWriteBytes(data.AsSpan(i + 24), forgedSize);
            int localHeader = (int)BitConverter.ToUInt32(data, i + 42);
            if (localHeader + 30 > data.Length) continue;
            // Local file header: uncompressed size at +22.
            BitConverter.TryWriteBytes(data.AsSpan(localHeader + 22), forgedSize);
            patched++;
        }

        Assert.Equal(1, patched);
        return data;
    }

    private static byte[] ForgedArchive() =>
        ForgeDeclaredSize(Zip((MemberName, new byte[PayloadBytes])), MemberName, ForgedSize);

    /// <summary>An unbounded read of a member's stream still stops at the declared size.</summary>
    [Fact]
    public void AMemberCannotYieldMoreThanItDeclared()
    {
        using var honest = new ZipArchive(new MemoryStream(Zip((MemberName, new byte[PayloadBytes]))));
        using var honestStream = honest.Entries[0].Open();
        using var honestBytes = new MemoryStream();
        honestStream.CopyTo(honestBytes);
        Assert.Equal(PayloadBytes, (int)honestBytes.Length);

        using var forged = new ZipArchive(new MemoryStream(ForgedArchive()));
        Assert.Equal(ForgedSize, (uint)forged.Entries[0].Length);

        using var forgedStream = forged.Entries[0].Open();
        using var forgedBytes = new MemoryStream();
        forgedStream.CopyTo(forgedBytes);
        Assert.Equal(ForgedSize, (uint)forgedBytes.Length);
    }

    /// <summary>End to end through the archive extractor: the forged member contributes the
    /// bytes it admitted to, not the megabyte it was hiding.</summary>
    [Fact]
    public void TheArchiveExtractorDoesNotInflateAForgedMember()
    {
        var doc = new ZipExtractor().Extract(ForgedArchive(), "application/zip", new ExtractionConfig());

        string content = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain).Content;
        Assert.True(content.Length < PayloadBytes / 2, $"extracted {content.Length} bytes");
    }
}
