using System.Text;
using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Cfb;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Native Word 97-2003 binary (.doc) extractor. Ports <c>extractors/doc.rs</c> +
/// <c>extraction/doc/mod.rs</c>: opens the OLE/CFB container, reads the FIB from the
/// <c>WordDocument</c> stream, walks the piece table (CLX/PlcPcd) in the table stream, decodes
/// CP1252 / UTF-16LE text, and applies the heuristic short-line heading detection.
/// </summary>
public sealed class DocExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/msword" };

    // Higher than the default so it wins over any generic handler for application/msword.
    public int Priority => 60;

    /// <summary>
    /// Index of the <c>fcClx</c>/<c>lcbClx</c> pair in the FIB's <c>FibRgFcLcb97</c> array.
    /// </summary>
    /// <remarks>
    /// [MS-DOC] 2.5.5 orders the array <c>fcStshfOrig</c>(0) … <c>fcSttbfAssoc</c>(32),
    /// <c>fcClx</c>(33). This read used index 66 (<c>fcBkdFtnOldOld</c>, an obsolete field Word
    /// writes as zero), so <c>fcClx == 0</c> held for every real document and the piece table was
    /// never walked — the whole <c>Clx</c> path was unreachable and only the contiguous fallback
    /// ever ran, reading <c>reserved5</c>/<c>reserved6</c> at <c>0x18</c>/<c>0x1C</c>, bytes
    /// [MS-DOC] says a reader must ignore (xberg-io/xberg#1551).
    /// </remarks>
    private const int FibFcLcbIdxClx = 33;

    // Indices into the FIB's `FibRgLw97` long-word array ([MS-DOC] 2.5.4).
    private const int FibLwIdxCcpText = 3;
    private const int FibLwIdxCcpFtn = 4;
    private const int FibLwIdxCcpHdd = 5;
    private const int FibLwIdxCcpAtn = 7;
    private const int FibLwIdxCcpEdn = 8;
    private const int FibLwIdxCcpTxbx = 9;
    private const int FibLwIdxCcpHdrTxbx = 10;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var comp = CompoundFile.Open(content);

        var meta = new OleUtil.OleMetadata();
        if (comp.TryReadStream("/\x05SummaryInformation") is { } si) OleUtil.ParseSummaryInfo(si, meta);
        if (comp.TryReadStream("/\x05DocumentSummaryInformation") is { } dsi) OleUtil.ParseSummaryInfo(dsi, meta);

        byte[] wordDoc = comp.TryReadStream("/WordDocument")
            ?? throw new InvalidDataException("Failed to open stream 'WordDocument'");
        if (wordDoc.Length < 12) throw new InvalidDataException("WordDocument stream too short");

        int wIdent = OleUtil.U16(wordDoc, 0);
        if (wIdent != 0xA5EC)
            throw new InvalidDataException($"Invalid DOC magic number: 0x{wIdent:X4}, expected 0xA5EC");

        int nFib = OleUtil.U16(wordDoc, 2);
        int flagsA = OleUtil.U16(wordDoc, 0x0A);
        bool use1Table = (flagsA & 0x0200) != 0;
        byte[] tableStream = comp.TryReadStream(use1Table ? "/1Table" : "/0Table") ?? Array.Empty<byte>();

        string text = nFib >= 101
            ? ExtractTextWord97(wordDoc, tableStream)
            : ExtractTextWord6(wordDoc);

        var doc = new InternalDocument("doc") { MimeType = mimeType };

        var additional = new Dictionary<string, JsonElement>();
        if (meta.RevisionNumber is { } rev) additional["revision"] = JsonStr(rev);
        additional["extraction_method"] = JsonStr("native_ole");

        List<string>? authors = meta.Author is { } a ? new List<string> { a } : null;
        doc.Metadata = new Metadata
        {
            Title = meta.Title,
            Subject = meta.Subject,
            Authors = authors,
            CreatedBy = meta.Author,
            ModifiedBy = meta.LastAuthor,
            Additional = additional,
        };

        var paragraphs = text.Split("\n\n");
        for (int i = 0; i < paragraphs.Length; i++)
        {
            string trimmed = paragraphs[i].Trim();
            if (trimmed.Length == 0) continue;

            bool isSingleLine = !trimmed.Contains('\n');
            bool isShort = trimmed.Length <= 80;
            bool noTrailingPunct = !trimmed.EndsWith('.') && !trimmed.EndsWith(':') && !trimmed.EndsWith(';');
            bool nextIsLonger = i + 1 < paragraphs.Length && paragraphs[i + 1].Trim() is { Length: > 0 } next
                && next.Length > trimmed.Length;

            if (isSingleLine && isShort && noTrailingPunct && nextIsLonger)
                doc.PushElement(InternalElement.TextElement(ElementKind.Heading(2), trimmed, 0));
            else
                doc.PushElement(InternalElement.TextElement(ElementKind.Paragraph, trimmed, 0));
        }

        return doc;
    }

    // ── FIB / piece-table parsing (extraction/doc/mod.rs) ────────────────────────
    private static string ExtractTextWord97(byte[] wordDoc, byte[] table)
    {
        const int fibBaseSize = 32;
        int cswOffset = fibBaseSize;
        if (wordDoc.Length < cswOffset + 2) throw new InvalidDataException("FIB too short for csw");
        int csw = OleUtil.U16(wordDoc, cswOffset);
        int rgWOffset = cswOffset + 2;
        int cslwOffset = rgWOffset + csw * 2;
        if (wordDoc.Length < cslwOffset + 2) throw new InvalidDataException("FIB too short for cslw");
        int cslw = OleUtil.U16(wordDoc, cslwOffset);
        int rgLwOffset = cslwOffset + 2;

        int ccpTextOffset = rgLwOffset + FibLwIdxCcpText * 4;
        if (wordDoc.Length < ccpTextOffset + 4) throw new InvalidDataException("FIB too short for ccpText");
        int ccpText = (int)OleUtil.U32(wordDoc, ccpTextOffset);

        var ranges = SubdocRanges.FromFib(wordDoc, rgLwOffset, ccpText);

        int cbrgOffset = rgLwOffset + cslw * 4;
        if (wordDoc.Length < cbrgOffset + 2) throw new InvalidDataException("FIB too short for cbRgFcLcb");
        int rgFcLcbOffset = cbrgOffset + 2;

        int fcClxOffset = rgFcLcbOffset + FibFcLcbIdxClx * 8;
        int lcbClxOffset = fcClxOffset + 4;
        if (wordDoc.Length < lcbClxOffset + 4) throw new InvalidDataException("FIB too short for fcClx/lcbClx");

        int fcClx = (int)OleUtil.U32(wordDoc, fcClxOffset);
        int lcbClx = (int)OleUtil.U32(wordDoc, lcbClxOffset);

        if (fcClx == 0 || lcbClx == 0)
            return ExtractTextContiguous(wordDoc, ccpText);
        if (table.Length < fcClx + lcbClx)
            throw new InvalidDataException("CLX extends beyond table stream");

        // Parse CLX: skip Prc entries (0x01), find Pcdt (0x02).
        int pos = fcClx;
        int clxEnd = fcClx + lcbClx;
        while (pos < clxEnd)
        {
            byte clxt = table[pos];
            if (clxt == 0x02)
            {
                pos += 1;
                if (pos + 4 > clxEnd) throw new InvalidDataException("Pcdt truncated at lcb");
                pos += 4; // lcb of PlcPcd
                return ExtractFromPieceTable(wordDoc, table, pos, clxEnd, ranges);
            }
            if (clxt == 0x01)
            {
                pos += 1;
                if (pos + 2 > clxEnd) break;
                int cbGrpprl = OleUtil.U16(table, pos);
                pos += 2 + cbGrpprl;
            }
            else break;
        }
        return ExtractTextFallback(wordDoc);
    }

    /// <summary>
    /// Walk the piece table (<c>PlcPcd</c>), bucketing each piece's characters into the
    /// subdocument CP range they fall in, and assemble the labelled sections.
    /// </summary>
    /// <remarks>
    /// Ports upstream's <c>extract_text_from_piece_table</c>. Any piece whose CP range started
    /// at or after <c>ccpText</c> — that is, every footnote, header/footer, comment and text-box
    /// piece — used to be skipped outright, so none of that content ever appeared
    /// (xberg-io/xberg#77).
    /// </remarks>
    private static string ExtractFromPieceTable(
        byte[] wordDoc, byte[] table, int plcStart, int plcEnd, SubdocRanges ranges)
    {
        int plcSize = plcEnd - plcStart;
        if (plcSize < 16) throw new InvalidDataException("PlcPcd too small");
        int n = (plcSize - 4) / 12;
        if (n == 0) return "";

        var main = new StringBuilder(ranges.Main.Length);
        var footnote = new StringBuilder();
        var header = new StringBuilder();
        var annotation = new StringBuilder();
        var textbox = new StringBuilder();

        for (int i = 0; i < n; i++)
        {
            int cpStartOff = plcStart + i * 4;
            int cpEndOff = plcStart + (i + 1) * 4;
            int pcdOff = plcStart + (n + 1) * 4 + i * 8;
            if (cpEndOff + 4 > plcEnd || pcdOff + 8 > plcEnd) break;

            int cpStart = (int)OleUtil.U32(table, cpStartOff);
            int cpEnd = (int)OleUtil.U32(table, cpEndOff);
            if (cpStart >= ranges.TotalCp) break;

            uint fcRaw = OleUtil.U32(table, pcdOff + 2);
            int charCount = Math.Max(0, cpEnd - cpStart);
            string piece = DecodePieceChars(wordDoc, fcRaw, charCount);
            if (piece.Length == 0) continue;

            AppendRangeOverlap(piece, cpStart, ranges.Main, main);
            AppendRangeOverlap(piece, cpStart, ranges.Footnote, footnote);
            AppendRangeOverlap(piece, cpStart, ranges.Header, header);
            AppendRangeOverlap(piece, cpStart, ranges.Annotation, annotation);
            AppendRangeOverlap(piece, cpStart, ranges.Textbox, textbox);
        }

        var content = new StringBuilder(NormalizeDocText(main.ToString()));
        foreach (var (label, section) in new[]
        {
            ("Footnotes", footnote),
            ("Headers and Footers", header),
            ("Comments", annotation),
            ("Text Boxes", textbox),
        })
        {
            string normalized = NormalizeDocText(section.ToString());
            if (normalized.Length == 0) continue;
            if (content.Length > 0) content.Append("\n\n");
            content.Append(label).Append("\n\n").Append(normalized);
        }
        return content.ToString();
    }

    /// <summary>Decode one piece's characters, choosing CP1252 or UTF-16LE from its FC.</summary>
    private static string DecodePieceChars(byte[] wordDoc, uint fcRaw, int charCount)
    {
        var sb = new StringBuilder(charCount);
        if ((fcRaw & 0x4000_0000) != 0)
        {
            int byteOffset = (int)(fcRaw & 0x3FFF_FFFF) / 2;
            int end = byteOffset + charCount;
            if (end <= wordDoc.Length)
                for (int k = byteOffset; k < end; k++) sb.Append(OleUtil.Cp1252ToChar(wordDoc[k]));
            return sb.ToString();
        }

        int utf16Offset = (int)(fcRaw & 0x3FFF_FFFF);
        int utf16End = utf16Offset + charCount * 2;
        if (utf16End <= wordDoc.Length)
            for (int k = utf16Offset; k + 1 < utf16End; k += 2)
                sb.Append((char)(ushort)(wordDoc[k] | (wordDoc[k + 1] << 8)));

        // Heuristic: a mostly-CJK decode means the compression bit was wrong → redo as CP1252.
        string piece = sb.ToString();
        int suspicious = piece.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        if (piece.Length > 4 && suspicious > piece.Length / 4)
        {
            sb.Clear();
            int end2 = utf16Offset + charCount;
            if (end2 <= wordDoc.Length)
                for (int k = utf16Offset; k < end2; k++) sb.Append(OleUtil.Cp1252ToChar(wordDoc[k]));
            return sb.ToString();
        }
        return piece;
    }

    /// <summary>Append the part of <paramref name="piece"/> that falls inside a CP range.</summary>
    private static void AppendRangeOverlap(string piece, int cpStart, SubdocRange range, StringBuilder outText)
    {
        if (range.Length == 0) return;
        int pieceEnd = cpStart + piece.Length;
        int overlapStart = Math.Max(cpStart, range.Start);
        int overlapEnd = Math.Min(pieceEnd, range.End);
        if (overlapStart < overlapEnd)
            outText.Append(piece, overlapStart - cpStart, overlapEnd - overlapStart);
    }

    /// <summary>A half-open CP-space range belonging to one subdocument.</summary>
    private readonly record struct SubdocRange(int Start, int End)
    {
        public int Length => Math.Max(0, End - Start);
    }

    /// <summary>
    /// The CP-space subdocument layout, derived from the FIB's <c>ccp*</c> fields.
    /// </summary>
    /// <remarks>
    /// Order matches the FIB's <c>FibRgLw97</c> field declaration order, which is also the
    /// document's physical CP-space layout ([MS-DOC] 2.4.2): main document, footnotes,
    /// headers/footers, comments, endnotes, text boxes, header text boxes. <c>ccpMcr</c> (the
    /// deprecated macro subdocument) is skipped: it does not occupy CP space. Endnotes and
    /// header text boxes are not extracted, but their spans must still be counted so later
    /// subdocuments resolve to the right offsets.
    /// </remarks>
    private readonly record struct SubdocRanges(
        SubdocRange Main, SubdocRange Footnote, SubdocRange Header,
        SubdocRange Annotation, SubdocRange Endnote, SubdocRange Textbox,
        SubdocRange HeaderTextbox)
    {
        public int TotalCp => HeaderTextbox.End;

        public static SubdocRanges FromFib(byte[] wordDoc, int rgLwOffset, int ccpText)
        {
            int Read(int index)
            {
                int off = rgLwOffset + index * 4;
                return wordDoc.Length >= off + 4 ? (int)OleUtil.U32(wordDoc, off) : 0;
            }

            var main = new SubdocRange(0, ccpText);
            var footnote = new SubdocRange(main.End, main.End + Read(FibLwIdxCcpFtn));
            var header = new SubdocRange(footnote.End, footnote.End + Read(FibLwIdxCcpHdd));
            var annotation = new SubdocRange(header.End, header.End + Read(FibLwIdxCcpAtn));
            var endnote = new SubdocRange(annotation.End, annotation.End + Read(FibLwIdxCcpEdn));
            var textbox = new SubdocRange(endnote.End, endnote.End + Read(FibLwIdxCcpTxbx));
            var headerTextbox = new SubdocRange(textbox.End, textbox.End + Read(FibLwIdxCcpHdrTxbx));
            return new SubdocRanges(main, footnote, header, annotation, endnote, textbox, headerTextbox);
        }
    }

    private static string ExtractTextContiguous(byte[] wordDoc, int ccpText)
    {
        if (wordDoc.Length < 0x20) return ExtractTextFallback(wordDoc);
        int fcMin = (int)OleUtil.U32(wordDoc, 0x18);
        int fcMac = (int)OleUtil.U32(wordDoc, 0x1C);
        if (fcMin == 0 || fcMin >= wordDoc.Length) return ExtractTextFallback(wordDoc);
        int dataLen = Math.Min(Math.Max(0, fcMac - fcMin), wordDoc.Length - fcMin);
        if (dataLen == 0) return ExtractTextFallback(wordDoc);

        int nullCount = 0;
        for (int i = fcMin; i < fcMin + dataLen; i++) if (wordDoc[i] == 0) nullCount++;
        bool isUnicode = dataLen >= ccpText * 2 || nullCount > dataLen / 4;

        var sb = new StringBuilder();
        if (isUnicode)
        {
            int taken = 0;
            for (int k = fcMin; k + 1 < fcMin + dataLen && taken < ccpText; k += 2, taken++)
            {
                ushort cu = (ushort)(wordDoc[k] | (wordDoc[k + 1] << 8));
                sb.Append((char)cu);
            }
        }
        else
        {
            int taken = 0;
            for (int k = fcMin; k < fcMin + dataLen && taken < ccpText; k++, taken++)
                sb.Append(OleUtil.Cp1252ToChar(wordDoc[k]));
        }
        string normalized = NormalizeDocText(sb.ToString());
        return normalized.Length == 0 ? ExtractTextFallback(wordDoc) : normalized;
    }

    private static string ExtractTextFallback(byte[] wordDoc)
    {
        var result = new StringBuilder();
        var run = new StringBuilder();
        for (int i = 256; i < wordDoc.Length; i++)
        {
            byte b = wordDoc[i];
            if (b == 0x0D || b == 0x0A || b == 0x09 || (b >= 0x20 && b <= 0xFE))
                run.Append(OleUtil.Cp1252ToChar(b));
            else if (run.Length > 0)
            {
                if (run.Length >= 3) { if (result.Length > 0) result.Append(' '); result.Append(run); }
                run.Clear();
            }
        }
        if (run.Length >= 3) { if (result.Length > 0) result.Append(' '); result.Append(run); }
        if (result.Length == 0) throw new InvalidDataException("No text content found in DOC file");
        return NormalizeDocText(result.ToString());
    }

    private static string ExtractTextWord6(byte[] wordDoc)
    {
        if (wordDoc.Length < 0x50) throw new InvalidDataException("Word 6/95 file too short");
        int ccpText = (int)OleUtil.U32(wordDoc, 0x4C);
        int fcMin = (int)OleUtil.U32(wordDoc, 0x18);
        if (fcMin + ccpText > wordDoc.Length) return ExtractTextFallback(wordDoc);
        var sb = new StringBuilder(ccpText);
        for (int k = fcMin; k < fcMin + ccpText; k++) sb.Append(OleUtil.Cp1252ToChar(wordDoc[k]));
        return NormalizeDocText(sb.ToString());
    }

    /// <summary>Word field BEGIN marker. Text from here to <see cref="FieldSeparator"/> is the
    /// field <em>instruction</em> (<c>HYPERLINK "…"</c>, <c>PAGEREF _Toc1 \h</c>,
    /// <c>TOC \o "1-3"</c>), i.e. markup.</summary>
    private const char FieldBegin = '\x13';

    /// <summary>Word field SEPARATOR marker. Text from here to <see cref="FieldEnd"/> is the field
    /// <em>result</em> — the only part a reader sees, and the only part that is document
    /// text.</summary>
    private const char FieldSeparator = '\x14';

    /// <summary>Word field END marker.</summary>
    private const char FieldEnd = '\x15';

    /// <summary>
    /// Word non-breaking hyphen (<c>0x1E</c> in the binary text stream), emitted as U+2011.
    /// </summary>
    /// <remarks>
    /// This is a <em>visible</em> character — the reader sees a hyphen; the only thing
    /// "non-breaking" suppresses is a line break at that position. Dropping it welds the two
    /// halves of a compound together (<c>twenty-one</c> → <c>twentyone</c>), corrupting the word
    /// rather than merely losing formatting. U+2011 rather than ASCII <c>-</c> so that the same
    /// document saved as <c>.doc</c> and as <c>.docx</c> extracts to the same text: the DOCX
    /// parser maps <c>w:noBreakHyphen</c> — the same character in the modern serialization of the
    /// same Word document model — to U+2011.
    /// </remarks>
    private const char NonBreakingHyphen = '\u2011';

    /// <summary>
    /// For each <see cref="FieldBegin"/> in <paramref name="text"/>, in order of occurrence,
    /// whether it has a matching <see cref="FieldEnd"/>.
    /// </summary>
    /// <remarks>
    /// Fields nest — a <c>TOC</c> result is full of <c>PAGEREF</c> fields — so matching is
    /// innermost-first via a stack. Begins left on the stack at end of input are unterminated and
    /// reported as <c>false</c>; the caller deliberately does <em>not</em> treat one as opening a
    /// suppression region, because doing so would swallow the entire remainder of a document whose
    /// stream happens to carry one stray <c>0x13</c>. Degrading to the historical behaviour (the
    /// instruction leaks) is far cheaper than losing the document tail.
    /// </remarks>
    private static bool[] ScanFieldBeginTermination(string text)
    {
        var terminated = new List<bool>();
        var open = new Stack<int>();

        foreach (char c in text)
        {
            if (c == FieldBegin)
            {
                terminated.Add(false);
                open.Push(terminated.Count - 1);
            }
            else if (c == FieldEnd && open.Count > 0)
            {
                // A stray END with nothing open is ignored rather than throwing: this is
                // user-supplied binary content.
                terminated[open.Pop()] = true;
            }
        }

        return terminated.ToArray();
    }

    /// <summary>
    /// Normalize extracted DOC text: strip field instructions, convert special characters and
    /// clean up whitespace.
    /// </summary>
    /// <remarks>
    /// Field handling (upstream GH#1460): the text between <see cref="FieldBegin"/> and
    /// <see cref="FieldSeparator"/> is the field instruction and is markup, not document text, so
    /// it is dropped; the result between <see cref="FieldSeparator"/> and <see cref="FieldEnd"/>
    /// is kept. A terminated field with no separator has no result and contributes nothing.
    /// </remarks>
    internal static string NormalizeDocText(string text)
    {
        var result = new StringBuilder(text.Length);

        bool[] beginTerminated = ScanFieldBeginTermination(text);
        int beginOrdinal = 0;
        // One entry per open field; true while that field is still in its instruction part.
        var fieldStack = new List<bool>();
        // Count of fieldStack entries still in their instruction part. Text is suppressed whenever
        // this is non-zero, which is what makes nesting work: a PAGEREF inside a TOC instruction
        // stays suppressed even after the inner field reaches its own separator.
        int instructionDepth = 0;

        foreach (char c in text)
        {
            if (c == FieldBegin)
            {
                bool terminated = beginOrdinal < beginTerminated.Length && beginTerminated[beginOrdinal];
                beginOrdinal++;
                if (terminated)
                {
                    fieldStack.Add(true);
                    instructionDepth++;
                }
                continue;
            }

            if (c == FieldSeparator)
            {
                if (fieldStack.Count > 0 && fieldStack[^1])
                {
                    fieldStack[^1] = false;
                    instructionDepth--;
                }
                continue;
            }

            if (c == FieldEnd)
            {
                if (fieldStack.Count > 0)
                {
                    bool inInstruction = fieldStack[^1];
                    fieldStack.RemoveAt(fieldStack.Count - 1);
                    if (inInstruction) instructionDepth--;
                }
                continue;
            }

            if (instructionDepth > 0) continue;

            switch (c)
            {
                case '\r': result.Append('\n'); break;
                case '\x07': result.Append('\t'); break;
                case '\x0B': result.Append('\n'); break;
                case '\x0C': result.Append('\n'); break;
                case '\x01' or '\x08': break;
                case '\x1E': result.Append(NonBreakingHyphen); break;
                // 0x1F is the *optional* (soft) hyphen: invisible unless the line happens to break
                // there, so discarding it is correct and must stay that way — emitting it would
                // insert a hyphen the reader never saw.
                default:
                    if (c < '\x20' && c != '\n' && c != '\t') break;
                    result.Append(c); break;
            }
        }

        var cleaned = new StringBuilder(result.Length);
        bool prevNl = false, prevPrevNl = false;
        foreach (char c in result.ToString())
        {
            if (c == '\n')
            {
                if (prevPrevNl && prevNl) continue;
                prevPrevNl = prevNl; prevNl = true;
            }
            else { prevPrevNl = false; prevNl = false; }
            cleaned.Append(c);
        }
        return cleaned.ToString().Trim();
    }

    private static JsonElement JsonStr(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
}
