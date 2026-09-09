using System.Text;

namespace XRay.Content.Internal.Pdf;

/// <summary>
/// Text repair passes applied to assembled PDF element text. Ports the subset of Rust
/// <c>pdf/structure/text_repair.rs</c> that the extractor runs over every element
/// (<c>pdf/structure/pipeline.rs</c>): contextual ligature repair, Unicode ligature
/// expansion, then Unicode punctuation normalization — in that order.
/// </summary>
internal static class PdfTextRepair
{
    /// <summary>Run the element-level repair chain, in Rust's order.</summary>
    public static string Repair(string text)
    {
        if (text.Length == 0) return text;
        string t = RepairContextualLigatures(text);
        t = ExpandLigaturesWithSpaceAbsorption(t);
        return NormalizeUnicodeText(t);
    }

    /// <summary>
    /// Repair ligature corruption using contextual heuristics. Some PDF fonts have broken
    /// ToUnicode CMaps that map ligature glyphs to punctuation: <c>!</c> → fi/ff,
    /// <c>"</c> → ffi, <c>#</c> → fi, <c>*</c> → tt.
    /// <para>
    /// Every rule is gated on its neighbours, so ordinary punctuation is untouched: there is
    /// deliberately no letter + <c>!</c> + end-of-string rule, because a sentence-final
    /// exclamation mark looks exactly like the corrupted form.
    /// </para>
    /// </summary>
    /// <remarks>
    /// There is no <c>:</c> → ti rule and no uppercase <c>M</c> → tti rule. Both characters
    /// occur constantly in healthy text — ratios, times, URLs, units like "nM", identifiers —
    /// so those arms corrupted ordinary words, turning "aMb" into "attib". They were introduced
    /// behind a per-font broken-CMap signal that no longer exists here, and were left
    /// unconditional when it went away. Re-add only alongside real document-level evidence
    /// (xberg-io/xberg#1556).
    /// </remarks>
    public static string RepairContextualLigatures(string text)
    {
        if (text.Length < 2) return text;

        var result = new StringBuilder(text.Length + 16);
        bool repaired = false;
        bool prevIsAlpha = false;
        bool prevIsSpaceOrStart = true;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            bool nextIsAlpha = char.IsLetter(next);
            bool nextIsLower = char.IsLower(next);
            bool nextIsVowel = next is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'I' or 'O' or 'U';

            switch (ch)
            {
                case '!' when prevIsAlpha && nextIsVowel:
                    result.Append("ff"); repaired = true; break;
                case '!' when prevIsAlpha && nextIsAlpha:
                    result.Append("fi"); repaired = true; break;
                case '"' when prevIsAlpha && nextIsAlpha:
                    result.Append("ffi"); repaired = true; break;
                case '#' when prevIsAlpha && nextIsAlpha:
                case '#' when prevIsSpaceOrStart && nextIsLower:
                    result.Append("fi"); repaired = true; break;
                case '!' when prevIsSpaceOrStart && nextIsLower:
                    result.Append("fi"); repaired = true; break;
                case '*' when prevIsAlpha && nextIsAlpha:
                    result.Append("tt"); repaired = true; break;
                default:
                    result.Append(ch); break;
            }

            prevIsAlpha = char.IsLetter(ch);
            prevIsSpaceOrStart = char.IsWhiteSpace(ch);
        }

        return repaired ? result.ToString() : text;
    }

    /// <summary>
    /// Expand Unicode ligature characters (U+FB00–U+FB06) to their ASCII equivalents,
    /// absorbing a spurious space between the ligature glyph and the rest of the word — PDFs
    /// often emit "ﬁ eld", which must come out as "field".
    /// </summary>
    public static string ExpandLigaturesWithSpaceAbsorption(string text)
    {
        if (text.AsSpan().IndexOfAny(Ligatures) < 0) return text;

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            string? expansion = text[i] switch
            {
                'ﬀ' => "ff",
                'ﬁ' => "fi",
                'ﬂ' => "fl",
                'ﬃ' => "ffi",
                'ﬄ' => "ffl",
                'ﬅ' => "st",
                'ﬆ' => "st",
                _ => null,
            };

            if (expansion is null) { result.Append(text[i]); continue; }

            result.Append(expansion);

            // Absorb a following space only when a word character continues after it.
            if (i + 2 < text.Length && text[i + 1] == ' '
                && (char.IsLetterOrDigit(text[i + 2]) || text[i + 2] == '_'))
                i++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Normalize Unicode punctuation to the ASCII forms the ground truth tokenizes on:
    /// curly quotes to straight, fraction slash to <c>/</c>, bullet to a middle dot.
    /// </summary>
    public static string NormalizeUnicodeText(string text)
    {
        if (text.AsSpan().IndexOfAny(NormalizePunctuation) < 0) return text;

        return text
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('⁄', '/')
            .Replace('•', '·');
    }

    /// <summary>
    /// The segment-level repair chain, applied to every segment before paragraphs are merged.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Repair"/>, which polishes assembled element text. Order is not
    /// arbitrary: encoding normalization must precede everything so control characters and soft
    /// hyphens are gone before anything reads neighbours; <see cref="CollapseSpacedHyphens"/>
    /// must run before <see cref="NormalizeUnicodeText"/>, while U+2010/U+2011 are still
    /// distinguishable from an ASCII hyphen.
    /// </remarks>
    public static string RepairSegment(string text, PdfTextRepairWitnesses witnesses)
    {
        if (text.Length == 0) return text;
        string t = NormalizeTextEncoding(text);
        t = RepairLigatureSpaces(t, witnesses);
        t = ExpandLigaturesWithSpaceAbsorption(t);
        t = CollapseSpacedHyphens(t);
        t = NormalizeUnicodeText(t);
        return CleanDuplicatePunctuation(t);
    }

    /// <summary>
    /// Remove the spurious space a decomposed ligature glyph leaves behind — "eff iciently",
    /// "signif icant", "f irst".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap appears at the ligature position because the extractor split the glyph into
    /// characters whose advance widths no longer add up. Only an <c>f</c> followed by a space and
    /// then <c>i</c>, <c>l</c>, or <c>f</c> qualifies.
    /// </para>
    /// <para>
    /// That same character pattern is also an ordinary word boundary whenever a word happens to
    /// end in <c>f</c> and the next happens to begin with <c>i</c>, <c>l</c> or <c>f</c> —
    /// "relief for", "itself infringes", "bedrijf is". At the string layer the two cases are
    /// indistinguishable; only whether the document itself uses the word elsewhere tells them
    /// apart, so the space survives when either fragment is attested in
    /// <paramref name="witnesses"/>. The guard this replaces tested a hard-coded list of 33 short
    /// English words against the <em>left</em> fragment only, so everything outside that list
    /// welded, English included (xberg-io/xberg#1591).
    /// </para>
    /// <para>
    /// Known limits, stated rather than silently accepted: a real word ending in <c>f</c> used
    /// exactly once in the whole document, right before an <c>i</c>/<c>l</c>/<c>f</c>-initial
    /// word, still welds; and a genuine ligature fragment that coincides with an attested word is
    /// left split. Both need a coincidence the static list could never have caught either.
    /// </para>
    /// </remarks>
    public static string RepairLigatureSpaces(string text, PdfTextRepairWitnesses witnesses)
    {
        if (!text.Contains("f ", StringComparison.Ordinal)) return text;

        var result = new StringBuilder(text.Length);
        int wordStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == 'f' && i + 1 < text.Length && text[i + 1] == ' ')
            {
                var rightWord = AlphabeticRunAt(text, i + 2);
                if (rightWord.Length > 0 && PdfTextRepairWitnesses.IsLigatureContinuation(rightWord[0]))
                {
                    var leftWord = text.AsSpan(wordStart, i - wordStart + 1);
                    bool weldsAWitnessedWord =
                        witnesses.IsWitnessedWord(leftWord) || witnesses.IsWitnessedWord(rightWord);
                    if (!weldsAWitnessedWord)
                    {
                        result.Append(ch);
                        i++; // swallow the space
                        continue;
                    }
                }
            }
            result.Append(ch);
            if (!char.IsLetter(ch)) wordStart = i + 1;
        }
        return result.Length == text.Length ? text : result.ToString();
    }

    /// <summary>The maximal run of letters starting at <paramref name="start"/>, possibly empty.</summary>
    private static ReadOnlySpan<char> AlphabeticRunAt(string text, int start)
    {
        if (start >= text.Length) return default;
        int end = start;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        return text.AsSpan(start, end - start);
    }

    /// <summary>
    /// Collapse spacing artifacts around Unicode hyphens between alphanumerics.
    /// </summary>
    /// <remarks>
    /// A hyphenated identifier rendered as separate PDF text runs — "DARPA", "‐", "BAA-15-58" —
    /// gets reassembled with kerning-gap spaces as <c>DARPA ‐ BAA ‐ 15</c>. A spaced U+2010/U+2011
    /// between alphanumerics is not a typographic construct (spaced dashes use en or em dashes),
    /// so it collapses. ASCII <c>-</c> and the longer dashes are left alone: a spaced ASCII hyphen
    /// can be a legitimate range or a minus sign.
    /// </remarks>
    public static string CollapseSpacedHyphens(string text)
    {
        if (text.AsSpan().IndexOfAny(UnicodeHyphens) < 0) return text;

        static bool IsGap(char c) => c is ' ' or '\u00A0' or '\n' or '\r' or '\t';

        var result = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                int j = i + 1;
                while (j < text.Length && IsGap(text[j])) j++;
                if (j > i + 1 && j < text.Length && (text[j] == '\u2010' || text[j] == '\u2011'))
                {
                    int k = j + 1;
                    while (k < text.Length && IsGap(text[k])) k++;
                    if (k > j + 1 && k < text.Length && char.IsLetterOrDigit(text[k]))
                    {
                        result.Append(text[i]).Append('-');
                        i = k;
                        continue;
                    }
                }
            }
            result.Append(text[i]);
            i++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Collapse <c>, ,</c> / <c>. .</c> / <c>; ;</c> / <c>: :</c> to a single mark.
    /// </summary>
    /// <remarks>
    /// Segment-level re-extraction picks up characters from an adjacent cell when bounding boxes
    /// overlap slightly, and the duplicate is almost always punctuation. Collapsing repeats until
    /// the text is stable, since three in a row leave a fresh pair behind.
    /// </remarks>
    public static string CleanDuplicatePunctuation(string text)
    {
        if (!HasDuplicatePunctuation(text)) return text;
        string current = CollapseDuplicatePunctuationOnce(text);
        while (HasDuplicatePunctuation(current)) current = CollapseDuplicatePunctuationOnce(current);
        return current;
    }

    private static bool IsDupPunct(char c) => c is ',' or '.' or ';' or ':';

    private static bool HasDuplicatePunctuation(string text)
    {
        for (int i = 0; i + 2 < text.Length; i++)
            if (IsDupPunct(text[i]) && text[i + 1] == ' ' && text[i + 2] == text[i]) return true;
        return false;
    }

    private static string CollapseDuplicatePunctuationOnce(string text)
    {
        var result = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (IsDupPunct(c) && i + 2 < text.Length && text[i + 1] == ' ' && text[i + 2] == c)
            {
                result.Append(c);
                i += 3;
            }
            else { result.Append(c); i++; }
        }
        return result.ToString();
    }

    /// <summary>
    /// Resolve soft hyphens, PDF word-break markers and control characters.
    /// </summary>
    /// <remarks>
    /// A soft hyphen at the end of a run became a real line break when the document was
    /// typeset, so it turns into <c>-</c> and lets the dehyphenator rejoin the fragments; one
    /// mid-run is an invisible hint and simply goes. <c>\x02</c> is what pdfium emits where a
    /// producer discarded the hyphen character outright, so it and the whitespace after it are
    /// dropped to rejoin the fragments directly. Other C0 controls carry no text.
    /// </remarks>
    public static string NormalizeTextEncoding(string text)
    {
        bool needsWork = false;
        foreach (char c in text)
            if (c == '\u00AD' || (c < 0x20 && c != '\t' && c != '\n' && c != '\r')) { needsWork = true; break; }
        if (!needsWork) return text;

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\u00AD')
            {
                bool atEnd = i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]);
                if (atEnd) result.Append('-');
                continue;
            }
            if (ch == '\x02')
            {
                while (i + 1 < text.Length && (text[i + 1] == ' ' || text[i + 1] == '\n')) i++;
                continue;
            }
            if (char.IsControl(ch) && ch != '\n' && ch != '\r' && ch != '\t') continue;
            result.Append(ch);
        }
        return result.ToString();
    }

    /// <summary>
    /// Rejoin words a PDF's glyph positioning split apart: a lone leading letter, a short
    /// fragment followed by a lowercase continuation, or a one- or two-letter tail.
    /// </summary>
    /// <remarks>
    /// Markdown table rows are left alone — a run of pipes and dashes is not prose, and joining
    /// its cells would corrupt the grid.
    /// </remarks>
    public static string RepairBrokenWordSpacing(string text)
    {
        if (text.Length == 0) return text;
        if (text.Contains("| --- |", StringComparison.Ordinal) || text.StartsWith('|')) return text;

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        bool hasJoinable = false;
        for (int i = 0; i + 1 < words.Length && !hasJoinable; i++)
        {
            hasJoinable = IsJoinableFragment(words[i], words[i + 1])
                || (words[i].All(char.IsLetter)
                    && !IsCommonShortWord(words[i])
                    && IsTrailingFragment(words[i + 1]));
        }
        if (!hasJoinable) return text;

        var result = new StringBuilder(text.Length);
        int w = 0;
        while (w < words.Length)
        {
            if (w > 0 && result.Length > 0) result.Append(' ');
            string word = words[w];

            // A single letter stranded before a lowercase word is that word's first letter.
            if (word.Length == 1
                && char.IsLetter(word[0])
                && !IsCommonShortWord(word)
                && w + 1 < words.Length
                && char.IsLower(words[w + 1][0]))
            {
                result.Append(word).Append(words[w + 1]);
                w += 2;
                continue;
            }

            if (w + 1 < words.Length && IsJoinableFragment(word, words[w + 1]))
            {
                result.Append(word);
                w++;
                int lastConsumedLen = Utf8Len(word);
                int totalConsumed = lastConsumedLen;
                while (w < words.Length)
                {
                    string next = words[w];
                    if (!char.IsLower(next[0])) break;
                    if (lastConsumedLen <= 3 && Utf8Len(next) <= 3)
                    {
                        result.Append(next);
                        lastConsumedLen = Utf8Len(next);
                        totalConsumed += lastConsumedLen;
                        w++;
                        continue;
                    }
                    if (totalConsumed <= 3) { result.Append(next); w++; }
                    break;
                }
                continue;
            }

            if (w + 1 < words.Length
                && word.All(char.IsLetter)
                && !IsCommonShortWord(word)
                && IsTrailingFragment(words[w + 1]))
            {
                result.Append(word);
                while (w + 1 < words.Length && IsTrailingFragment(words[w + 1]))
                {
                    w++;
                    result.Append(words[w]);
                }
                w++;
                continue;
            }

            result.Append(word);
            w++;
        }

        string repaired = result.ToString();
        return repaired == string.Join(" ", words) ? text : repaired;
    }

    /// <summary>A one- or two-letter lowercase tail split off the end of a word.</summary>
    private static bool IsTrailingFragment(string word) =>
        Utf8Len(word) <= 2
        && word.Length > 0
        && word.All(c => char.IsLower(c) && char.IsLetter(c))
        && !IsCommonShortWord(word);

    /// <summary>A short alphabetic fragment followed by a lowercase continuation.</summary>
    private static bool IsJoinableFragment(string word, string next) =>
        Utf8Len(word) <= 3
        && word.Length > 0
        && word.All(char.IsLetter)
        && !word.All(char.IsUpper)
        && !IsCommonShortWord(word)
        && next.Length > 0
        && char.IsLower(next[0]);

    private static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// Whether a word is one of the short function words a repair must not swallow, which keeps
    /// <see cref="RepairBrokenWordSpacing"/> from absorbing a real word.
    /// </summary>
    /// <remarks>
    /// This list used to be the only guard on <see cref="RepairLigatureSpaces"/> too, tested
    /// against the left fragment alone; it now judges by what the document itself attests
    /// instead, since no fixed list can decide a boundary that depends on the text at hand.
    /// </remarks>
    private static bool IsCommonShortWord(ReadOnlySpan<char> word) =>
        CommonShortWords.Contains(word.ToString());

    private static readonly HashSet<string> CommonShortWords = new(StringComparer.Ordinal)
    {
        "a", "A", "I", "an", "am", "as", "at", "be", "by", "do", "go", "he", "if", "in", "is",
        "it", "me", "my", "no", "of", "oh", "on", "or", "so", "to", "up", "us", "we", "An", "Am",
        "As", "At", "Be", "By", "Do", "Go", "He", "If", "In", "Is", "It", "Me", "My", "No", "Of",
        "Oh", "On", "Or", "So", "To", "Up", "Us", "We", "the", "and", "are", "but", "can", "did",
        "for", "got", "had", "has", "her", "him", "his", "how", "its", "let", "may", "new", "nor",
        "not", "now", "old", "one", "our", "out", "own", "ran", "say", "she", "too", "two", "use",
        "was", "way", "who", "why", "yet", "you", "all", "any", "big", "day", "end", "far", "few",
        "put", "run", "saw", "set", "top", "try", "win", "yes", "The", "And", "Are", "But", "Can",
        "Did", "For", "Got", "Had", "Has", "Her", "Him", "His", "How", "Its", "Let", "May", "New",
        "Nor", "Not", "Now", "Old", "One", "Our", "Out", "Own", "Ran", "Say", "She", "Too", "Two",
        "Use", "Was", "Way", "Who", "Why", "Yet", "You", "All", "Any", "Big", "Day", "End", "Far",
        "Few", "Put", "Run", "Saw", "Set", "Top", "Try", "Win", "Yes"
    };

    private static readonly char[] UnicodeHyphens = { '\u2010', '\u2011' };

    private static readonly char[] Ligatures =
        { 'ﬀ', 'ﬁ', 'ﬂ', 'ﬃ', 'ﬄ', 'ﬅ', 'ﬆ' };

    private static readonly char[] NormalizePunctuation =
        { '‘', '’', '“', '”', '⁄', '•' };
}
