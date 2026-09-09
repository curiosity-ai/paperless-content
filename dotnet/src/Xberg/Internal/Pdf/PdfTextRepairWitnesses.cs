namespace Xberg.Internal.Pdf;

/// <summary>
/// Document-scoped evidence the text-repair passes need to tell a genuine word or hyphen apart
/// from an extraction artefact that looks identical at the string layer.
/// </summary>
/// <remarks>
/// Ports upstream's <c>TextRepairWitnesses</c>. Both collectors read every page's segments before
/// any of them are consumed, because a witness on one page can be the sole evidence for a repair
/// decision on another.
/// </remarks>
internal sealed class PdfTextRepairWitnesses
{
    /// <summary>
    /// <c>(left, right)</c> word pairs the document itself writes as one hyphenated token.
    /// </summary>
    public HashSet<(string Left, string Right)> Hyphens { get; } = new();

    /// <summary>Lowercased standalone alphabetic words the document writes elsewhere.</summary>
    public HashSet<string> Words { get; } = new(StringComparer.Ordinal);

    /// <summary>An empty set, for the paths that have no document to gather evidence from.</summary>
    public static PdfTextRepairWitnesses Empty { get; } = new();

    /// <summary>
    /// Minimum letters on each side of a mid-run hyphen before it is recorded, so initials and
    /// bullet dashes cannot mint spurious pairs.
    /// </summary>
    private const int MinHyphenWitnessWordLength = 2;

    /// <summary>
    /// Minimum letters a fragment needs before it counts as evidence of a standalone word. A
    /// single decomposed-ligature fragment — the bare <c>f</c> in <c>f irst</c> — is common, and
    /// must never witness itself.
    /// </summary>
    public const int MinLigatureWitnessWordLength = 2;

    /// <summary>Gather both kinds of witness from every page's segments in one pass.</summary>
    public static PdfTextRepairWitnesses Collect(List<List<SegmentData>> allPageSegments)
    {
        var witnesses = new PdfTextRepairWitnesses();
        foreach (var page in allPageSegments)
            foreach (var segment in page)
            {
                witnesses.CollectHyphens(segment.Text);
                witnesses.CollectWords(segment.Text);
            }
        return witnesses;
    }

    /// <summary>
    /// Record <c>(left, right)</c> pairs around every hyphen that is not the last character of
    /// the run, so a genuine authored compound can be told from a hyphen that merely fell at a
    /// line-wrap boundary (xberg-io/xberg#1543).
    /// </summary>
    /// <remarks>
    /// Only a strictly mid-run hyphen can witness a real compound: a line-wrap hyphen is by
    /// construction the final character before the break, so scanning those would witness the
    /// very artefact this exists to judge.
    /// </remarks>
    private void CollectHyphens(string text)
    {
        if (text.Length < 3) return;
        for (int position = 1; position < text.Length - 1; position++)
        {
            if (text[position] != '-') continue;

            int leftStart = position;
            while (leftStart > 0 && char.IsLetter(text[leftStart - 1])) leftStart--;
            int rightEnd = position + 1;
            while (rightEnd < text.Length && char.IsLetter(text[rightEnd])) rightEnd++;

            int leftLength = position - leftStart;
            int rightLength = rightEnd - (position + 1);
            if (leftLength < MinHyphenWitnessWordLength || rightLength < MinHyphenWitnessWordLength) continue;

            Hyphens.Add((
                text.Substring(leftStart, leftLength).ToLowerInvariant(),
                text.Substring(position + 1, rightLength).ToLowerInvariant()));
        }
    }

    /// <summary>
    /// Record standalone alphabetic words, so the ligature-space repair can tell a real word
    /// boundary from a decomposed-ligature gap (xberg-io/xberg#1591).
    /// </summary>
    /// <remarks>
    /// A token that is itself one half of a ligature-space candidate — ending in <c>f</c> right
    /// before whitespace, or starting with <c>i</c>/<c>l</c>/<c>f</c> right after it — is not
    /// independent evidence for that occurrence: the very space under judgment put it there, so
    /// counting it would make every candidate witness itself and disable the repair. The same
    /// word written elsewhere, in a position that is not itself a candidate, still counts.
    /// </remarks>
    private void CollectWords(string text)
    {
        var cores = new List<string>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            cores.Add(TrimToLetters(token));

        for (int index = 0; index < cores.Count; index++)
        {
            string core = cores[index];
            if (core.Length < MinLigatureWitnessWordLength) continue;

            bool isLeftOfCandidate = core[^1] == 'f'
                && index + 1 < cores.Count
                && cores[index + 1].Length > 0
                && IsLigatureContinuation(cores[index + 1][0]);
            bool isRightOfCandidate = index > 0
                && cores[index - 1].Length > 0
                && cores[index - 1][^1] == 'f'
                && IsLigatureContinuation(core[0]);
            if (isLeftOfCandidate || isRightOfCandidate) continue;

            Words.Add(core.ToLowerInvariant());
        }
    }

    /// <summary>The letters that can follow an <c>f</c> in a decomposed ligature.</summary>
    public static bool IsLigatureContinuation(char c) => c is 'i' or 'l' or 'f';

    /// <summary>Whether the word is long enough to count and is attested in the document.</summary>
    public bool IsWitnessedWord(ReadOnlySpan<char> word) =>
        word.Length >= MinLigatureWitnessWordLength
        && Words.Contains(word.ToString().ToLowerInvariant());

    private static string TrimToLetters(string token)
    {
        int start = 0, end = token.Length;
        while (start < end && !char.IsLetter(token[start])) start++;
        while (end > start && !char.IsLetter(token[end - 1])) end--;
        return token.Substring(start, end - start);
    }
}
