using XRay.Content.Core;
using XRay.Content.Extractors;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// The legacy <c>.doc</c> piece table (<c>Clx</c>/<c>PlcPcd</c>) path, built end to end from a
/// synthesized OLE2 container. Ports upstream's <c>extraction::doc</c> FIB tests, which the port
/// previously had no way to run: there is no <c>.doc</c> fixture in the tree and no CFB writer,
/// so every test here goes through <see cref="CfbBuilder"/>.
/// </summary>
public sealed class DocPieceTableTests
{
    private const string DocMime = "application/msword";

    // FIB layout constants matching upstream's `build_fib` test helper.
    private const int FibBase = 32;
    private const int Csw = 14;
    private const int Cslw = 22;
    private const int FibLwIdxCcpText = 3;
    private const int FibLwIdxCcpFtn = 4;
    private const int FibLwIdxCcpAtn = 7;

    private static int RgLwOffset => FibBase + 2 + Csw * 2 + 2;
    private static int RgFcLcbOffset => RgLwOffset + Cslw * 4 + 2;

    private static void WriteU16(byte[] buf, int offset, ushort value) =>
        BitConverter.TryWriteBytes(buf.AsSpan(offset), value);

    private static void WriteU32(byte[] buf, int offset, uint value) =>
        BitConverter.TryWriteBytes(buf.AsSpan(offset), value);

    /// <summary>Build a <paramref name="length"/>-byte WordDocument-stream FIB header.</summary>
    private static byte[] BuildFib(int length, uint ccpText, uint ccpFtn = 0, uint ccpAtn = 0)
    {
        var buf = new byte[length];
        WriteU16(buf, 0, 0xA5EC);            // wIdent
        WriteU16(buf, 2, 101);               // nFib ≥ 101 selects the Word97+ path
        WriteU16(buf, 0x0A, 0x0200);         // fWhichTblStm: use 1Table
        WriteU16(buf, FibBase, Csw);
        WriteU16(buf, FibBase + 2 + Csw * 2, Cslw);
        WriteU32(buf, RgLwOffset + FibLwIdxCcpText * 4, ccpText);
        WriteU32(buf, RgLwOffset + FibLwIdxCcpFtn * 4, ccpFtn);
        WriteU32(buf, RgLwOffset + FibLwIdxCcpAtn * 4, ccpAtn);
        return buf;
    }

    private readonly record struct Piece(uint CpStart, uint CpEnd, uint FcRaw);

    /// <summary>A compressed (CP1252, one byte per character) FC pointing at a stream offset.</summary>
    private static uint CompressedFc(uint byteOffset) => 0x40000000u | (byteOffset * 2);

    private static byte[] BuildPlcPcd(params Piece[] pieces)
    {
        var buf = new List<byte>();
        foreach (var p in pieces) buf.AddRange(BitConverter.GetBytes(p.CpStart));
        buf.AddRange(BitConverter.GetBytes(pieces[^1].CpEnd));
        foreach (var p in pieces)
        {
            buf.AddRange([(byte)0, (byte)0]);
            buf.AddRange(BitConverter.GetBytes(p.FcRaw));
            buf.AddRange([(byte)0, (byte)0]);
        }
        return buf.ToArray();
    }

    /// <summary>Wrap a <c>PlcPcd</c> in a <c>Clx</c> and return the matching 1Table bytes.</summary>
    private static byte[] BuildTableStream(byte[] plcPcd, uint fcClx)
    {
        var clx = new List<byte> { 0x02 };                 // Pcdt marker
        clx.AddRange(BitConverter.GetBytes(0u));           // lcb (unused by the reader)
        clx.AddRange(plcPcd);
        var table = new List<byte>(new byte[fcClx]);
        table.AddRange(clx);
        return table.ToArray();
    }

    private static string Extract(byte[] doc) =>
        Derive.DeriveExtractionResult(
            new DocExtractor().Extract(doc, DocMime, new ExtractionConfig()),
            includeDocumentStructure: false,
            OutputFormat.Plain).Content;

    /// <summary>
    /// Upstream <c>fix(doc): read fcClx at FibRgFcLcb97 pair 33, not obsolete pair 66</c>
    /// (xberg-io/xberg#1551). Pair 66 is <c>fcBkdFtnOldOld</c>, which Word writes as zero, so
    /// <c>fcClx == 0</c> held for every real document: the piece table was never walked and
    /// extraction always took the contiguous fallback, which reads <c>reserved5</c>/
    /// <c>reserved6</c> at <c>0x18</c>/<c>0x1C</c> — bytes [MS-DOC] says a reader must ignore.
    /// </summary>
    /// <remarks>
    /// The pair index is written here as a literal rather than through the reader's own
    /// constant, deliberately: a test that positioned the <c>Clx</c> through the same constant
    /// would move with a regression and stay green.
    /// </remarks>
    [Fact]
    public void FcClxIsReadAtMsDocPair33NotTheObsoletePair66()
    {
        const int SpecPairIndex = 33;
        const int ObsoletePairTheReaderUsedToUse = 66;
        const string Text = "lorem ipsum dolor sit amet";
        // Placed where the contiguous fallback looks, so the two paths cannot be confused for
        // one another: whichever string comes back names the path that ran.
        const string FallbackDecoy = "FALLBACK DECOY TEXT NOT THE DOCUMENT BODY";
        const int TextOffset = 2048;
        const int DecoyOffset = 1536;
        const uint FcClx = 8;

        byte[] wordDoc = BuildFib(TextOffset + Text.Length, (uint)Text.Length);
        System.Text.Encoding.ASCII.GetBytes(Text).CopyTo(wordDoc, TextOffset);
        System.Text.Encoding.ASCII.GetBytes(FallbackDecoy).CopyTo(wordDoc, DecoyOffset);

        // reserved5 / reserved6 — what the fallback reads as fcMin / fcMac.
        WriteU32(wordDoc, 0x18, DecoyOffset);
        WriteU32(wordDoc, 0x1C, (uint)(DecoyOffset + FallbackDecoy.Length));

        byte[] plcPcd = BuildPlcPcd(new Piece(0, (uint)Text.Length, CompressedFc(TextOffset)));
        byte[] table = BuildTableStream(plcPcd, FcClx);

        int specPair = RgFcLcbOffset + SpecPairIndex * 8;
        WriteU32(wordDoc, specPair, FcClx);
        WriteU32(wordDoc, specPair + 4, (uint)(table.Length - FcClx));

        int obsoletePair = RgFcLcbOffset + ObsoletePairTheReaderUsedToUse * 8;
        Assert.Equal(0u, BitConverter.ToUInt32(wordDoc, obsoletePair));

        string content = Extract(CfbBuilder.Build(("WordDocument", wordDoc), ("1Table", table)));

        Assert.Equal(Text, content);
        Assert.DoesNotContain("FALLBACK DECOY", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Footnotes, headers, comments and text boxes live in subdocument CP ranges addressed by
    /// the FIB's <c>ccpFtn</c>/<c>ccpAtn</c> fields, past <c>ccpText</c>. Reachable only once
    /// the piece table above is walked at all.
    /// </summary>
    [Fact]
    public void FootnoteAndCommentSubdocumentsAreExtracted()
    {
        const string MainText = "Hello";
        const string FootnoteText = "Note one";
        const string CommentText = "See me";
        const uint FcClx = 8;
        const int MainOffset = 900, FootnoteOffset = 950, CommentOffset = 1000;

        uint ccpText = (uint)MainText.Length;
        uint ccpFtn = (uint)FootnoteText.Length;
        uint ccpAtn = (uint)CommentText.Length;

        byte[] wordDoc = BuildFib(2048, ccpText, ccpFtn, ccpAtn);
        System.Text.Encoding.ASCII.GetBytes(MainText).CopyTo(wordDoc, MainOffset);
        System.Text.Encoding.ASCII.GetBytes(FootnoteText).CopyTo(wordDoc, FootnoteOffset);
        System.Text.Encoding.ASCII.GetBytes(CommentText).CopyTo(wordDoc, CommentOffset);

        byte[] plcPcd = BuildPlcPcd(
            new Piece(0, ccpText, CompressedFc(MainOffset)),
            new Piece(ccpText, ccpText + ccpFtn, CompressedFc(FootnoteOffset)),
            new Piece(ccpText + ccpFtn, ccpText + ccpFtn + ccpAtn, CompressedFc(CommentOffset)));
        byte[] table = BuildTableStream(plcPcd, FcClx);

        int specPair = RgFcLcbOffset + 33 * 8;
        WriteU32(wordDoc, specPair, FcClx);
        WriteU32(wordDoc, specPair + 4, (uint)(table.Length - FcClx));

        string content = Extract(CfbBuilder.Build(("WordDocument", wordDoc), ("1Table", table)));

        Assert.Contains(MainText, content, StringComparison.Ordinal);
        Assert.Contains(FootnoteText, content, StringComparison.Ordinal);
        Assert.Contains(CommentText, content, StringComparison.Ordinal);
    }
}
