from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from scripts.sync_supported_counts import (
    Counts,
    TextFile,
    parse_registry,
    synchronize_files,
    tracked_text_files,
    verify_files,
)

REGISTRY = """
static FORMATS: &[FormatEntry] = &[
    FormatEntry {
        extensions: &["txt"],
        mime_type: "text/plain",
        aliases: &[],
    },
    FormatEntry {
        extensions: &["md", "markdown"],
        mime_type: "text/markdown",
        aliases: &["text/x-markdown"],
    },
];
"""


def _tracked_files(root: Path, *paths: str) -> list[TextFile]:
    subprocess.run(["git", "init", "-q"], cwd=root, check=True)
    subprocess.run(["git", "add", "--", *paths], cwd=root, check=True)
    return tracked_text_files(root)


def test_parse_registry_derives_format_and_unique_extension_counts() -> None:
    assert parse_registry(REGISTRY) == Counts(formats=2, extensions=3)


def test_parse_registry_rejects_duplicate_extensions() -> None:
    duplicate = REGISTRY.replace('["md", "markdown"]', '["txt", "markdown"]')
    with pytest.raises(ValueError, match=r"duplicate extension.*txt"):
        parse_registry(duplicate)


def test_parse_registry_rejects_malformed_entries() -> None:
    malformed = REGISTRY.replace('mime_type: "text/plain",', "")
    with pytest.raises(ValueError, match="malformed FormatEntry"):
        parse_registry(malformed)


def test_verify_reports_every_stale_publication_phrase() -> None:
    files = [
        TextFile(
            Path("README.md"),
            "Xberg extracts 101 formats across 115 file extensions.\n",
        ),
        TextFile(Path("Cargo.toml"), 'description = "Document extraction from 101 formats"\n'),
    ]
    errors, advertisements = verify_files(Counts(100, 120), files)
    assert advertisements == 3
    assert errors == [
        "README.md:1: advertises 101 formats; registry has 100",
        "README.md:1: advertises 115 file extensions; registry has 120",
        "Cargo.toml:1: advertises 101 formats; registry has 100",
    ]


def test_verify_ignores_unrelated_format_counts() -> None:
    files = [
        TextFile(Path("README.md"), "Xberg extracts 100 formats.\n"),
        TextFile(Path("ATTRIBUTIONS.md"), "Pandoc baselines cover 6 formats.\n"),
        TextFile(Path("migration.md"), "Unstructured supports about 30 formats.\n"),
        TextFile(Path("image.rs"), "JPEG 2000 and JBIG2 formats are decoded.\n"),
    ]
    errors, advertisements = verify_files(Counts(100, 120), files)
    assert errors == []
    assert advertisements == 1


def test_verify_recognizes_supports_file_formats() -> None:
    errors, advertisements = verify_files(
        Counts(100, 120),
        [
            TextFile(
                Path("tool.py"),
                'description = "Extract content. Supports 101 file formats."\n',
            )
        ],
    )
    assert advertisements == 1
    assert errors == ["tool.py:1: advertises 101 formats; registry has 100"]


def test_verify_recognizes_document_formats_and_mime_extension_claims() -> None:
    errors, advertisements = verify_files(
        Counts(100, 120),
        [
            TextFile(Path("reader.ts"), "Reader for 101 document formats powered by Xberg.\n"),
            TextFile(Path("lib.rs"), "Comprehensive MIME type detection (119 file extensions)\n"),
        ],
    )
    assert advertisements == 2
    assert errors == [
        "reader.ts:1: advertises 101 formats; registry has 100",
        "lib.rs:1: advertises 119 file extensions; registry has 120",
    ]


def test_verify_does_not_self_attribute_competitor_document_or_mime_claims() -> None:
    errors, advertisements = verify_files(
        Counts(100, 120),
        [
            TextFile(
                Path("comparison.md"),
                "Xberg supports 100 document formats; Competitor supports 80 document formats.\n"
                "Xberg supports 100 formats; Competitor MIME supports 80 file extensions.\n",
            )
        ],
    )
    assert errors == []
    assert advertisements == 2


def test_verify_does_not_borrow_context_from_an_adjacent_competitor_claim() -> None:
    errors, advertisements = verify_files(
        Counts(100, 120),
        [
            TextFile(
                Path("comparison.md"),
                "Xberg supports 100 file formats.\nCompetitor supports 80 file formats.\n",
            )
        ],
    )
    assert errors == []
    assert advertisements == 1


def test_verify_attributes_same_line_format_claims_to_their_own_clause() -> None:
    errors, advertisements = verify_files(
        Counts(100, 120),
        [
            TextFile(
                Path("comparison.md"),
                "Xberg supports 100 file formats; Competitor supports 80 file formats.\n",
            )
        ],
    )
    assert errors == []
    assert advertisements == 1


def test_verify_attributes_same_line_extension_claims_to_their_own_clause() -> None:
    errors, advertisements = verify_files(
        Counts(100, 120),
        [
            TextFile(
                Path("comparison.md"),
                "Xberg supports 100 formats across 120 file extensions; Competitor supports 80 file extensions.\n",
            )
        ],
    )
    assert errors == []
    assert advertisements == 2


def test_verify_excludes_historical_and_fixture_categories() -> None:
    files = [
        TextFile(Path("README.md"), "Xberg extracts 100 formats.\n"),
        TextFile(Path("CHANGELOG.md"), "Xberg previously extracted 88 formats.\n"),
        TextFile(Path("tests/fixtures/result.md"), "Xberg extracts 77 formats.\n"),
        TextFile(Path("vendor/project/README.md"), "Xberg extracts 66 formats.\n"),
    ]
    errors, advertisements = verify_files(Counts(100, 120), files)
    assert errors == []
    assert advertisements == 1


def test_verify_ignores_tracked_test_scripts(tmp_path: Path) -> None:
    (tmp_path / "README.md").write_text("Xberg supports 100 formats.\n", encoding="utf-8")
    test_script = tmp_path / "scripts" / "test_counts.py"
    test_script.parent.mkdir()
    test_script.write_text('CLAIM = "Xberg supports 77 formats"\n', encoding="utf-8")
    files = _tracked_files(tmp_path, "README.md", "scripts/test_counts.py")
    errors, advertisements = verify_files(Counts(100, 120), files)
    assert errors == []
    assert advertisements == 1


def test_verify_rejects_a_scan_that_finds_no_advertisements() -> None:
    errors, advertisements = verify_files(
        Counts(100, 120),
        [TextFile(Path("README.md"), "No numerical claims here.\n")],
    )
    assert advertisements == 0
    assert errors == ["no supported-format advertisements found in tracked UTF-8 files"]


def test_synchronize_updates_claims_without_touching_unrelated_counts(
    tmp_path: Path,
) -> None:
    readme = tmp_path / "README.md"
    readme.write_text(
        "Xberg extracts 101 formats across 115 file extensions.\nPandoc baselines cover 6 formats.\n",
        encoding="utf-8",
    )
    changed, advertisements = synchronize_files(
        Counts(100, 120),
        [TextFile(Path("README.md"), readme.read_text(encoding="utf-8"))],
        tmp_path,
    )
    assert changed == [Path("README.md")]
    assert advertisements == 2
    assert readme.read_text(encoding="utf-8") == (
        "Xberg extracts 100 formats across 120 file extensions.\nPandoc baselines cover 6 formats.\n"
    )
    unchanged, second_advertisements = synchronize_files(
        Counts(100, 120),
        [TextFile(Path("README.md"), readme.read_text(encoding="utf-8"))],
        tmp_path,
    )
    assert unchanged == []
    assert second_advertisements == 2


def test_synchronize_ignores_small_extension_counts(tmp_path: Path) -> None:
    readme = tmp_path / "README.md"
    original = "Xberg supports 100 formats and a helper supports 3 file extensions.\n"
    readme.write_text(original, encoding="utf-8")
    changed, advertisements = synchronize_files(
        Counts(100, 120),
        [TextFile(Path("README.md"), original)],
        tmp_path,
    )
    assert changed == []
    assert advertisements == 1
    assert readme.read_text(encoding="utf-8") == original


def test_synchronize_skips_generated_ownership_and_updates_sources(
    tmp_path: Path,
) -> None:
    generated = tmp_path / "README.md"
    generated.write_text(
        "<!-- This file is auto-generated by alef — DO NOT EDIT. -->\nXberg extracts 101 formats.\n",
        encoding="utf-8",
    )
    hashed_generated = tmp_path / "generated.md"
    hashed_generated.write_text("<!-- alef:hash:abc -->\nXberg extracts 101 formats.\n", encoding="utf-8")
    handwritten = tmp_path / "docs" / "alef.md"
    handwritten.parent.mkdir()
    handwritten.write_text(
        "Xberg extracts 101 formats.\nOne.\nTwo.\nThree.\nFour.\nThis prose is not generated by Alef.\n",
        encoding="utf-8",
    )
    source = tmp_path / "templates" / "readme" / "root.md"
    source.parent.mkdir(parents=True)
    source.write_text("Xberg extracts 101 formats.\n", encoding="utf-8")
    plugin = tmp_path / "plugin" / "package.json"
    plugin.parent.mkdir()
    plugin.write_text('{"description":"Xberg extracts 101 formats."}\n', encoding="utf-8")
    marketplace = tmp_path / ".claude-plugin" / "marketplace.json"
    marketplace.parent.mkdir()
    marketplace.write_text('{"description":"Xberg extracts 101 formats."}\n', encoding="utf-8")
    agent_marketplace = tmp_path / ".agents" / "plugins" / "marketplace.json"
    agent_marketplace.parent.mkdir(parents=True)
    agent_marketplace.write_text('{"description":"Xberg extracts 101 formats."}\n', encoding="utf-8")
    files = [
        TextFile(Path("README.md"), generated.read_text(encoding="utf-8")),
        TextFile(Path("generated.md"), hashed_generated.read_text(encoding="utf-8")),
        TextFile(Path("docs/alef.md"), handwritten.read_text(encoding="utf-8")),
        TextFile(Path("templates/readme/root.md"), source.read_text(encoding="utf-8")),
        TextFile(Path("plugin/package.json"), plugin.read_text(encoding="utf-8")),
        TextFile(
            Path(".claude-plugin/marketplace.json"),
            marketplace.read_text(encoding="utf-8"),
        ),
        TextFile(
            Path(".agents/plugins/marketplace.json"),
            agent_marketplace.read_text(encoding="utf-8"),
        ),
    ]
    changed, advertisements = synchronize_files(Counts(100, 120), files, tmp_path)
    assert changed == [Path("docs/alef.md"), Path("templates/readme/root.md")]
    assert advertisements == 7
    assert "101 formats" in generated.read_text(encoding="utf-8")
    assert "101 formats" in hashed_generated.read_text(encoding="utf-8")
    assert "100 formats" in handwritten.read_text(encoding="utf-8")
    assert "100 formats" in source.read_text(encoding="utf-8")
    assert "101 formats" in plugin.read_text(encoding="utf-8")
    assert "101 formats" in marketplace.read_text(encoding="utf-8")
    assert "101 formats" in agent_marketplace.read_text(encoding="utf-8")


def test_synchronize_rejects_a_symlink_destination_without_touching_its_target(
    tmp_path: Path,
) -> None:
    root = tmp_path / "repo"
    root.mkdir()
    outside = tmp_path / "outside.md"
    outside.write_text("outside\n", encoding="utf-8")
    (root / "README.md").symlink_to(outside)
    with pytest.raises(ValueError, match="regular file"):
        synchronize_files(
            Counts(100, 120),
            [TextFile(Path("README.md"), "Xberg supports 101 formats.\n")],
            root,
        )
    assert outside.read_text(encoding="utf-8") == "outside\n"


def test_synchronize_rejects_paths_outside_the_repository_root(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    root.mkdir()
    outside = tmp_path / "outside.md"
    outside.write_text("outside\n", encoding="utf-8")
    with pytest.raises(ValueError, match="outside repository root"):
        synchronize_files(
            Counts(100, 120),
            [TextFile(Path("../outside.md"), "Xberg supports 101 formats.\n")],
            root,
        )
    assert outside.read_text(encoding="utf-8") == "outside\n"


def test_tracked_text_files_rejects_publication_symlinks(tmp_path: Path) -> None:
    target = tmp_path / "target.md"
    target.write_text("Xberg supports 100 formats.\n", encoding="utf-8")
    (tmp_path / "README.md").symlink_to(target)
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True)
    subprocess.run(["git", "add", "--", "README.md"], cwd=tmp_path, check=True)
    with pytest.raises(ValueError, match="tracked symlink"):
        tracked_text_files(tmp_path)


def test_tracked_text_files_rejects_worktree_symlink_for_index_regular_file(
    tmp_path: Path,
) -> None:
    readme = tmp_path / "README.md"
    readme.write_text("Xberg supports 100 formats.\n", encoding="utf-8")
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True)
    subprocess.run(["git", "add", "--", "README.md"], cwd=tmp_path, check=True)
    target = tmp_path / "target.md"
    target.write_text("Xberg supports 77 formats.\n", encoding="utf-8")
    readme.unlink()
    readme.symlink_to(target)
    with pytest.raises(ValueError, match="working-tree file is not regular"):
        tracked_text_files(tmp_path)


def test_tracked_text_files_rejects_invalid_utf8_publication_text(
    tmp_path: Path,
) -> None:
    (tmp_path / "README.md").write_bytes(b"Xberg\xff")
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True)
    subprocess.run(["git", "add", "--", "README.md"], cwd=tmp_path, check=True)
    with pytest.raises(ValueError, match="invalid UTF-8"):
        tracked_text_files(tmp_path)


def test_tracked_text_files_skips_known_binary_content(tmp_path: Path) -> None:
    (tmp_path / "image.png").write_bytes(b"\x89PNG\r\n\xff")
    files = _tracked_files(tmp_path, "image.png")
    assert files == []
