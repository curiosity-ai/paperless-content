using System;
using System.IO;
using System.Linq;
using XRay.Content.Core;
using Xunit;

namespace XRay.Content.Tests;

public class XRayOptionsTests
{
    [Fact]
    public void DefaultsAreTheShippedBehaviour()
    {
        var options = new XRayOptions();
        Assert.True(options.UsePortedPdfSpans);
        Assert.Equal(25, options.PdfBaseSeconds);
        Assert.Equal(50.0, options.PdfMillisecondsPerPage);
        Assert.Equal(3600, options.PdfMaxSecondsPerDocument);
    }

    [Theory]
    [InlineData(0, 25.0)]        // floor: even a zero-page document gets the base
    [InlineData(1, 25.05)]
    [InlineData(1962, 123.1)]    // algebra_topology, which needs ~39 s
    [InlineData(4778, 263.9)]    // the Intel SDM, which needs ~55 s
    public void BudgetScalesWithPageCount(int pageCount, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, new XRayOptions().PdfBudgetSeconds(pageCount), 3);
    }

    [Fact]
    public void BudgetIsCappedHoweverManyPages()
    {
        var options = new XRayOptions();
        Assert.Equal(3600, options.PdfBudgetSeconds(10_000_000));
        // The cap binds from roughly 71,500 pages up; below that the scaling is live.
        Assert.True(options.PdfBudgetSeconds(70_000) < 3600);
    }

    [Fact]
    public void BudgetCoversTheCorpusWorstCasesWithHeadroom()
    {
        // Measured wall clock for a full extraction of each, on a quiet machine.
        var options = new XRayOptions();
        Assert.True(options.PdfBudgetSeconds(4778) > 55.0 * 2, "Intel SDM needs ~55 s");
        Assert.True(options.PdfBudgetSeconds(1962) > 39.0 * 2, "algebra_topology needs ~39 s");
    }

    [Fact]
    public void NegativePageCountFallsBackToTheBase()
    {
        Assert.Equal(25.0, new XRayOptions().PdfBudgetSeconds(-1), 3);
    }

    [Fact]
    public void ExtractionConfigPicksUpTheAmbientDefault()
    {
        var previous = XRayOptions.Default;
        try
        {
            XRayOptions.Default = new XRayOptions { PdfMaxSecondsPerDocument = 300 };
            Assert.Equal(300, new ExtractionConfig().Options.PdfMaxSecondsPerDocument);
        }
        finally
        {
            XRayOptions.Default = previous;
        }
    }

    [Fact]
    public void PerCallOptionsOverrideTheAmbientDefault()
    {
        var previous = XRayOptions.Default;
        try
        {
            XRayOptions.Default = new XRayOptions { PdfMaxSecondsPerDocument = 300 };
            var config = new ExtractionConfig { Options = new XRayOptions { PdfMaxSecondsPerDocument = 5 } };
            Assert.Equal(5, config.Options.PdfMaxSecondsPerDocument);
        }
        finally
        {
            XRayOptions.Default = previous;
        }
    }

    [Fact]
    public void NonPositiveCapDisablesTheGuard()
    {
        // A cap of "0 seconds" would otherwise mean "every document is already too late".
        Assert.Equal(long.MaxValue, new XRayOptions { PdfMaxSecondsPerDocument = 0 }.PdfDeadlineFromNow(10));
        Assert.Equal(long.MaxValue, new XRayOptions { PdfMaxSecondsPerDocument = -1 }.PdfDeadlineFromNow(10));
    }

    [Fact]
    public void DeadlineIsInTheFutureAndGrowsWithPageCount()
    {
        long now = DateTime.UtcNow.Ticks;
        var options = new XRayOptions();
        long small = options.PdfDeadlineFromNow(1);
        long large = options.PdfDeadlineFromNow(5000);

        Assert.True(small > now);
        Assert.True(large > small);
    }

    [Theory]
    [InlineData("XRAY_OXIDE_SPANS", null, true)]
    [InlineData("XRAY_OXIDE_SPANS", "", true)]
    [InlineData("XRAY_OXIDE_SPANS", "0", false)]
    [InlineData("XRAY_OXIDE_SPANS", "false", false)]
    [InlineData("XRAY_OXIDE_SPANS", "no", false)]
    [InlineData("XRAY_OXIDE_SPANS", "1", true)]
    [InlineData("XBERG_OXIDE_SPANS", null, true)]
    [InlineData("XBERG_OXIDE_SPANS", "", true)]
    [InlineData("XBERG_OXIDE_SPANS", "0", false)]
    [InlineData("XBERG_OXIDE_SPANS", "false", false)]
    [InlineData("XBERG_OXIDE_SPANS", "no", false)]
    [InlineData("XBERG_OXIDE_SPANS", "1", true)]
    public void FromEnvironmentReadsFlagsUnderEitherPrefix(string name, string? value, bool expected)
    {
        WithEnvironment(name, value, () =>
            Assert.Equal(expected, XRayOptions.FromEnvironment().UsePortedPdfSpans));
    }

    /// <summary>
    /// Both names reach every knob. A harness driving this port and the Rust original from one
    /// set of variables sets the <c>XBERG_</c> names; one driving only this port sets
    /// <c>XRAY_</c>. Neither may be the privileged spelling.
    /// </summary>
    [Theory]
    [InlineData("XRAY_")]
    [InlineData("XBERG_")]
    public void EveryKnobAnswersToBothPrefixes(string prefix)
    {
        WithEnvironment(prefix + "PDF_BASE_SECONDS", "90", () =>
        WithEnvironment(prefix + "PDF_MS_PER_PAGE", "12.5", () =>
        WithEnvironment(prefix + "PDF_MAX_SECONDS", "300", () =>
        WithEnvironment(prefix + "OXIDE_SPANS", "0", () =>
        {
            var options = XRayOptions.FromEnvironment();
            Assert.Equal(90, options.PdfBaseSeconds);
            Assert.Equal(12.5, options.PdfMillisecondsPerPage);
            Assert.Equal(300, options.PdfMaxSecondsPerDocument);
            Assert.False(options.UsePortedPdfSpans);
        }))));
    }

    /// <summary>
    /// <c>XRAY_</c> is this package's own name, so it wins over the compatibility alias when a
    /// process carries both — otherwise a stale <c>XBERG_</c> left in an environment would
    /// quietly override the variable the caller actually set.
    /// </summary>
    [Fact]
    public void TheXRayPrefixWinsOverTheXbergAlias()
    {
        WithEnvironment("XBERG_PDF_MAX_SECONDS", "300", () =>
        WithEnvironment("XRAY_PDF_MAX_SECONDS", "600", () =>
            Assert.Equal(600, XRayOptions.FromEnvironment().PdfMaxSecondsPerDocument)));
    }

    /// <summary>
    /// The value is resolved by name before it is parsed, so a malformed <c>XRAY_</c> value
    /// leaves the default in place rather than falling through to whatever <c>XBERG_</c> holds.
    /// Falling through would make the effective value depend on a variable the caller did not
    /// touch, which is exactly the surprise the precedence rule exists to prevent.
    /// </summary>
    [Fact]
    public void AMalformedXRayValueDoesNotFallThroughToTheAlias()
    {
        WithEnvironment("XBERG_PDF_MAX_SECONDS", "300", () =>
        WithEnvironment("XRAY_PDF_MAX_SECONDS", "not-a-number", () =>
            Assert.Equal(3600, XRayOptions.FromEnvironment().PdfMaxSecondsPerDocument)));
    }

    /// <summary>An empty value is unset, so the alias still applies.</summary>
    [Fact]
    public void AnEmptyXRayValueDefersToTheAlias()
    {
        WithEnvironment("XBERG_PDF_MAX_SECONDS", "300", () =>
        WithEnvironment("XRAY_PDF_MAX_SECONDS", "", () =>
            Assert.Equal(300, XRayOptions.FromEnvironment().PdfMaxSecondsPerDocument)));
    }

    [Theory]
    [InlineData("300", 300)]
    [InlineData("0", 0)]
    [InlineData(null, 3600)]
    [InlineData("not-a-number", 3600)]   // unparseable leaves the default in place
    public void FromEnvironmentReadsNumbers(string? value, int expected)
    {
        WithEnvironment("XBERG_PDF_MAX_SECONDS", value, () =>
            Assert.Equal(expected, XRayOptions.FromEnvironment().PdfMaxSecondsPerDocument));
    }

    [Theory]
    [InlineData("10", 10.0)]
    [InlineData("0.5", 0.5)]            // fractional ms/page must survive the round trip
    [InlineData(null, 50.0)]
    public void FromEnvironmentReadsThePerPageAllowance(string? value, double expected)
    {
        WithEnvironment("XBERG_PDF_MS_PER_PAGE", value, () =>
            Assert.Equal(expected, XRayOptions.FromEnvironment().PdfMillisecondsPerPage));
    }

    [Fact]
    public void FromEnvironmentReadsTheBase()
    {
        WithEnvironment("XBERG_PDF_BASE_SECONDS", "90", () =>
            Assert.Equal(90, XRayOptions.FromEnvironment().PdfBaseSeconds));
    }

    [Fact]
    public void FromEnvironmentLeavesUnsetKnobsAtTheirDefaults()
    {
        // A harness varying one variable must not silently reset the others.
        WithEnvironment("XBERG_PDF_MAX_SECONDS", "300", () =>
        {
            WithEnvironment("XBERG_OXIDE_SPANS", null, () =>
            {
                var options = XRayOptions.FromEnvironment();
                Assert.Equal(300, options.PdfMaxSecondsPerDocument);
                Assert.True(options.UsePortedPdfSpans);
                Assert.Equal(25, options.PdfBaseSeconds);
                Assert.Equal(50.0, options.PdfMillisecondsPerPage);
            });
        });
    }

    /// <summary>
    /// The whole point of the options class: library code must not consult ambient process
    /// state. Only the opt-in <see cref="XRayOptions.FromEnvironment"/> factory may.
    /// </summary>
    [Fact]
    public void LibraryCodeNeverReadsTheEnvironment()
    {
        string? root = FindLibrarySourceRoot();
        Assert.True(root is not null, "could not locate the XRay.Content library sources from the test binary");

        var offenders = Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && Path.GetFileName(f) != "Options.cs")
            .Where(f => File.ReadAllText(f).Contains("GetEnvironmentVariable"))
            .Select(f => Path.GetRelativePath(root!, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "library code must take configuration through XRayOptions, not the environment; found: "
            + string.Join(", ", offenders));
    }

    private static string? FindLibrarySourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "XRay.Content");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void WithEnvironment(string name, string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
