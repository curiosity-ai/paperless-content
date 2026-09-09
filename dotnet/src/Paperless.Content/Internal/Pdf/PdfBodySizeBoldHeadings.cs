namespace Paperless.Content.Internal.Pdf;

/// <summary>
/// Promotes bold body-size paragraphs to headings when a document uses them, repeatedly, as its
/// section titles.
/// </summary>
/// <remarks>
/// <para>
/// Font-size clustering has nothing to work with in a document whose headings are set in the body
/// face and distinguished only by weight — a house style common in reports, minutes and agendas.
/// Every section title in such a document arrives as an ordinary paragraph, and the whole outline
/// is lost.
/// </para>
/// <para>
/// Weight alone is far too weak a signal to act on paragraph by paragraph, so the decision is made
/// for the document as a whole: the candidates must be numerous, and they must outnumber whatever
/// headings were found by other means. A handful of bold run-in emphases in an otherwise
/// conventionally-headed document therefore changes nothing, while a document that titles thirty
/// sections this way gets all thirty.
/// </para>
/// <para>
/// Ports upstream's <c>promote_repeated_body_size_bold_headings</c> and the eligibility rules
/// around it (xberg-io/xberg <c>fix(pdf): classify repeated bold body-size headings</c>,
/// <c>… keep subordinate bold text out of headings</c>, <c>… preserve agenda heading hierarchy</c>,
/// <c>… narrow same-row heading suppression</c>, <c>… reject invalid heading geometry</c>).
/// </para>
/// </remarks>
internal static class PdfBodySizeBoldHeadings
{
    /// <summary>How many eligible paragraphs a document needs before the pattern is believed.</summary>
    private const int MinBodySizeBoldSignals = 3;

    /// <summary>
    /// How far the signals must outnumber the headings found by other means. A document that
    /// already has a conventional outline is not re-headed from weight.
    /// </summary>
    private const int MaxOtherHeadingRatio = 2;

    /// <summary>
    /// How far left of the block it opens a title may still sit, in ems of the body size. A real
    /// section title is flush with, or outdented from, the text beneath it.
    /// </summary>
    private const float AlignmentToleranceEm = 1.5f;

    /// <summary>Words the following block needs before it counts as a body block being opened.</summary>
    private const int MinOpenedBodyWords = 4;

    /// <summary>
    /// Vertical overlap, as a fraction of the shorter block's height, at which two blocks are
    /// reading as one row rather than as a title above its text.
    /// </summary>
    private const float SameRowMinVerticalOverlapRatio = 0.5f;

    /// <summary>Promote across the whole document, or leave every paragraph alone.</summary>
    public static void Promote(List<List<PdfParagraph>> allPages, float? bodyFontSize)
    {
        if (bodyFontSize is not { } body) return;

        var eligible = Eligibility(allPages, body);
        int signals = eligible.Sum(page => page.Count(e => e));
        int otherHeadings = allPages.Sum(page => page.Count(p => p.HeadingLevel is not null));
        if (signals < MinBodySizeBoldSignals || signals <= otherHeadings * MaxOtherHeadingRatio) return;

        for (int p = 0; p < allPages.Count; p++)
            for (int i = 0; i < allPages[p].Count; i++)
                if (eligible[p][i])
                    allPages[p][i].HeadingLevel = 3;
    }

    /// <summary>
    /// Which paragraphs would be titles if the document turns out to use this pattern.
    /// </summary>
    /// <remarks>
    /// Walked backwards so each candidate can see the content that follows it, which is what
    /// separates a title from a bold phrase inside running text: a title opens a block of body
    /// text beneath it and to its right. An explicitly numbered section is exempt from needing
    /// that evidence — its own numbering is the evidence — and is rejected only when it shares a
    /// text row with what follows, which means it is a run-in label rather than a title.
    /// </remarks>
    private static List<List<bool>> Eligibility(List<List<PdfParagraph>> allPages, float bodyFontSize)
    {
        var eligibility = new List<List<bool>>(allPages.Count);
        for (int i = 0; i < allPages.Count; i++) eligibility.Add(new List<bool>());

        (int Page, PdfParagraph Para)? nextContent = null;
        for (int pageIndex = allPages.Count - 1; pageIndex >= 0; pageIndex--)
        {
            var page = allPages[pageIndex];
            var pageEligibility = new bool[page.Count];
            for (int i = page.Count - 1; i >= 0; i--)
            {
                var paragraph = page[i];
                bool isCandidate = IsCandidate(paragraph, bodyFontSize);
                bool isExplicitSection = IsExplicitNumberedSection(PdfStructure.ParagraphPlainText(paragraph).Trim());

                // Geometry that is not finite cannot support either test below, and a promotion
                // made without it would be a guess dressed as a measurement.
                bool nonFiniteGeometry =
                    (paragraph.BlockBbox is { } ownBbox && !IsFinite(ownBbox))
                    || (nextContent is { } n1 && n1.Para.BlockBbox is { } nextBbox && !IsFinite(nextBbox));

                pageEligibility[i] = isCandidate && !nonFiniteGeometry && (isExplicitSection
                    ? !(nextContent is { } n2 && SharesTextRow(paragraph, n2.Para, pageIndex == n2.Page))
                    : nextContent is { } n3
                      && OpensAlignedBodyBlock(paragraph, n3.Para, bodyFontSize, pageIndex == n3.Page));

                if (paragraph.WordCount > 0 && !paragraph.IsPageFurniture)
                    nextContent = (pageIndex, paragraph);
            }
            eligibility[pageIndex] = pageEligibility.ToList();
        }
        return eligibility;
    }

    /// <summary>
    /// Whether a paragraph carries the weight-and-shape signal at all — before any question about
    /// what surrounds it.
    /// </summary>
    private static bool IsSignal(PdfParagraph para, float bodyFontSize)
    {
        if (para.HeadingLevel is not null || !para.IsBold || para.IsListItem || para.IsCodeBlock
            || para.IsFormula || para.IsPageFurniture || para.Lines.Count != 1
            || !float.IsFinite(bodyFontSize) || bodyFontSize <= 0f
            || Math.Abs(para.DominantFontSize - bodyFontSize) > 0.5f
            || para.WordCount > PdfStructure.MAX_BOLD_HEADING_WORD_COUNT)
            return false;

        string trimmed = PdfStructure.ParagraphPlainText(para).Trim();
        return trimmed.Length > 0
            // A sentence-ending period says prose, unless the text is shaped like a numbered or
            // legal section, which legitimately ends in one.
            && (!EndsWithSentencePeriod(trimmed) || PdfStructure.IsSectionPattern(trimmed))
            // A trailing colon introduces what follows rather than titling it — except in an
            // all-caps label, where it is the house style.
            && (!trimmed.EndsWith(':') || IsAllCaps(trimmed))
            && !PdfStructure.LooksLikeFigureLabel(trimmed)
            && !PdfStructure.LooksLikeBareUrl(trimmed)
            && !PdfStructure.IsSeparatorText(trimmed);
    }

    /// <summary>
    /// A candidate needs more than two words: one or two bold words are far more often a run-in
    /// emphasis than a section title.
    /// </summary>
    private static bool IsCandidate(PdfParagraph para, float bodyFontSize) =>
        IsSignal(para, bodyFontSize) && para.WordCount > 2;

    /// <summary>
    /// Whether a numbered heading is explicit enough to stand on its own numbering. A bare roman
    /// numeral followed by mixed-case prose is ordinary text that happens to open with a letter
    /// the numeral alphabet also uses ("I have…"), so it is only accepted when what follows is
    /// itself all-caps.
    /// </summary>
    private static bool IsExplicitNumberedSection(string text)
    {
        string trimmed = text.Trim();
        if (!PdfStructure.IsNumberedSectionHeading(trimmed)) return false;

        int romanPrefix = 0;
        while (romanPrefix < trimmed.Length && "IVXLCDM".IndexOf(trimmed[romanPrefix]) >= 0) romanPrefix++;
        if (romanPrefix == 0 || romanPrefix >= trimmed.Length || trimmed[romanPrefix] != ' ') return true;

        string remainder = trimmed[romanPrefix..].TrimStart();
        var letters = remainder.Where(char.IsLetter).ToList();
        return letters.Count > 0 && letters.All(char.IsUpper);
    }

    private static bool IsFinite((float L, float B, float R, float T) bbox) =>
        float.IsFinite(bbox.L) && float.IsFinite(bbox.B) && float.IsFinite(bbox.R) && float.IsFinite(bbox.T);

    /// <summary>Whether two blocks overlap vertically enough to be reading as one row.</summary>
    private static bool SharesTextRow(PdfParagraph candidate, PdfParagraph following, bool isSamePage)
    {
        if (!isSamePage) return false;
        if (candidate.BlockBbox is not { } a || following.BlockBbox is not { } b) return false;
        if (!IsFinite(a) || !IsFinite(b)) return false;

        float aBottom = Math.Min(a.B, a.T), aTop = Math.Max(a.B, a.T);
        float bBottom = Math.Min(b.B, b.T), bTop = Math.Max(b.B, b.T);
        float minHeight = Math.Min(aTop - aBottom, bTop - bBottom);
        float overlap = Math.Min(aTop, bTop) - Math.Max(aBottom, bBottom);
        return minHeight > 0f && Math.Max(overlap, 0f) / minHeight >= SameRowMinVerticalOverlapRatio;
    }

    /// <summary>
    /// Whether the paragraph opens a block of body text beneath it — the evidence that separates a
    /// title from a bold phrase in running text.
    /// </summary>
    /// <remarks>
    /// The block must be real prose (not another bold signal, not already a heading, long enough
    /// to be a paragraph) and must not sit further left than the title, since a title is flush
    /// with or outdented from what it titles. Where either block has no geometry at all the text
    /// evidence stands alone; where it has geometry that is not finite, nothing does.
    /// </remarks>
    private static bool OpensAlignedBodyBlock(
        PdfParagraph candidate, PdfParagraph following, float bodyFontSize, bool isSamePage)
    {
        if (following.WordCount < MinOpenedBodyWords
            || following.HeadingLevel is not null
            || IsSignal(following, bodyFontSize))
            return false;

        if ((candidate.BlockBbox is { } cb && !IsFinite(cb))
            || (following.BlockBbox is { } fb && !IsFinite(fb)))
            return false;

        if (candidate.BlockBbox is not { } candidateBbox || following.BlockBbox is not { } followingBbox)
            return true;

        if (SharesTextRow(candidate, following, isSamePage)) return false;

        float candidateLeft = Math.Min(candidateBbox.L, candidateBbox.R);
        float followingLeft = Math.Min(followingBbox.L, followingBbox.R);
        return candidateLeft <= followingLeft + bodyFontSize * AlignmentToleranceEm;
    }

    private static bool EndsWithSentencePeriod(string text)
    {
        string t = text.TrimEnd();
        return t.EndsWith('.') && !t.EndsWith("..", StringComparison.Ordinal);
    }

    private static bool IsAllCaps(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        return letters.Count >= 2 && letters.Count(char.IsUpper) / (double)letters.Count > 0.8;
    }
}
