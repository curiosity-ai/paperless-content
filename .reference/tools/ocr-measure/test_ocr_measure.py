"""Focused regressions for the OCR measurement tools."""

import subprocess
from pathlib import Path

from score_gt_lines import normalise, scorable_lines, score

TOOLS_DIR = Path(__file__).parent
BASH = Path("/bin/bash").resolve()


def test_should_remove_successive_markdown_list_markers() -> None:
    """A bullet followed by an escaped ordinal matches unlisted ground truth."""
    escaped = r"- 4\. Minimum lot area is 20,000 square feet."
    result = score(escaped, "Minimum lot area is 20,000 square feet.")

    assert normalise(escaped) == "minimum lot area is 20,000 square feet."  # noqa: S101
    assert result["recovered"] == 1  # noqa: S101
    assert result["unmatched"] == 0  # noqa: S101


def test_should_preserve_backslashes_inside_inline_code() -> None:
    """Backslashes remain literal inside inline code spans."""
    assert normalise(r"Run `grep '4\.' file` now") == r"run grep '4\.' file now"  # noqa: S101


def test_should_preserve_backslashes_inside_fenced_code() -> None:
    """Backslashes remain literal between matching fenced-code delimiters."""
    raw = "before\\.\n```text\ngrep '4\\.' file\n```\nafter\\."

    assert scorable_lines(raw) == [  # noqa: S101
        (r"before\.", "before."),
        (r"grep '4\.' file", r"grep '4\.' file"),
        (r"after\.", "after."),
    ]


def test_should_preserve_shorter_backtick_run_inside_longer_fence() -> None:
    """A shorter backtick run is code content, not Markdown scaffolding."""
    raw = "````text\n```literal fence content\nC:\\source\\report.txt\n````"

    assert scorable_lines(raw) == [  # noqa: S101
        ("```literal fence content", "literal fence content"),
        (r"C:\source\report.txt", r"c:\source\report.txt"),
    ]


def test_should_preserve_content_inside_tilde_fence() -> None:
    """Tilde fences preserve code backslashes and exclude their delimiters."""
    raw = "~~~~text\ngrep '4\\.' C:\\source\\report.txt\n~~~~"

    assert scorable_lines(raw) == [  # noqa: S101
        (r"grep '4\.' C:\source\report.txt", r"grep '4\.' c:\source\report.txt"),
    ]


def test_should_preserve_non_markdown_path_backslashes() -> None:
    """Backslashes before ordinary path characters are not Markdown escapes."""
    assert normalise(r"Open C:\Users\Goldziher\report.txt") == r"open c:\users\goldziher\report.txt"  # noqa: S101


def test_should_fail_fast_when_filter_has_no_runtime_toggle() -> None:
    """The obsolete A/B refuses to emit results and gives a reproducible alternative."""
    script = (TOOLS_DIR / "ab_line_filter.sh").resolve()
    result = subprocess.run(
        [BASH, str(script), "input.pdf", "ground-truth.txt", "output"],
        cwd=TOOLS_DIR,
        capture_output=True,
        check=False,
        text=True,
    )

    assert result.returncode == 2  # noqa: S101
    assert result.stdout == ""  # noqa: S101
    assert result.stderr == (  # noqa: S101
        "FATAL: the per-line dictionary filter has no runtime configuration toggle.\n"
        "To compare safely, run each leg from a distinct recorded source revision and record each binary SHA-256.\n"
        "Before EACH leg, clear both the OCR cache and extraction cache; never reuse cache entries between "
        "same-version binaries.\n"
    )
