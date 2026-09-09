using Xunit;

namespace XRay.Tests;

/// <summary>
/// One loud failure when the fixture corpus is absent, in place of the roughly thirty tests that
/// otherwise return early and report green.
/// </summary>
/// <remarks>
/// <para>
/// Most fixture-backed tests here open with <c>if (path is null) return;</c>, so without
/// <c>test_documents</c> they pass without asserting anything. That is not hypothetical: an
/// upstream sync ran a whole session against a corpus-less checkout, read "2097 passing" as a
/// clean result, and only found <c>OxStructureOrderTests.ATaggedRtlFormYieldsNoSpatialTable</c>
/// failing once the corpus was fetched — it had been silently skipping the entire time.
/// </para>
/// <para>
/// Upstream takes the stronger line, asserting per fixture that it exists and telling the reader
/// to fetch the corpus rather than skip. This is the same position stated once: the suite says
/// plainly that it is running degraded, instead of thirty tests each quietly deciding not to.
/// </para>
/// </remarks>
public sealed class CorpusPresenceTests
{
    /// <summary>The corpus root, relative to the test assembly's output directory.</summary>
    internal static string CorpusRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents"));

    /// <summary>A fixture only the binary fetch provides, so a bare submodule checkout is caught
    /// as well as a missing one — the text fixtures are in git, the binaries are not.</summary>
    private const string BinaryFixture = "doc/unit_test_lists.doc";

    [Fact]
    public void TheFixtureCorpusIsPresent()
    {
        Assert.True(Directory.Exists(CorpusRoot),
            $"the fixture corpus is not checked out at {CorpusRoot}. Every fixture-backed test in "
            + "this suite will pass without asserting anything. Run:\n"
            + "    git submodule update --init --depth 1 test_documents\n"
            + "    python3 test_documents/scripts/fetch_corpus.py");

        Assert.True(File.Exists(Path.Combine(CorpusRoot, BinaryFixture)),
            $"the corpus is checked out but its binaries are not: {BinaryFixture} is missing. The "
            + "submodule holds only the text fixtures; every office, PDF, epub and image fixture "
            + "comes from the bucket. Run:\n"
            + "    python3 test_documents/scripts/fetch_corpus.py");
    }
}
