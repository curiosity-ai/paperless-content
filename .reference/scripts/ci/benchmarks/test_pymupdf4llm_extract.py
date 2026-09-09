from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
ADAPTER_PATH = REPOSITORY_ROOT / "tools/benchmark-harness/scripts/pymupdf4llm_extract.py"
SPEC = importlib.util.spec_from_file_location("pymupdf4llm_extract", ADAPTER_PATH)
assert SPEC is not None and SPEC.loader is not None
PYMUPDF4LLM_EXTRACT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PYMUPDF4LLM_EXTRACT)


def test_no_ocr_preserves_existing_pdf_text_layer() -> None:
    document = REPOSITORY_ROOT / "test_documents/pdf/google_doc_document.pdf"

    result = PYMUPDF4LLM_EXTRACT.extract_sync(str(document), ocr_enabled=False)

    assert "Beautiful is better than ugly" in result["content"]


@pytest.mark.parametrize("ocr_enabled", [False, True])
def test_ocr_choice_is_forwarded_to_pymupdf4llm(monkeypatch: pytest.MonkeyPatch, ocr_enabled: bool) -> None:
    captured_options: dict[str, object] = {}

    def fake_to_markdown(file_path: str, **options: object) -> str:
        captured_options.update(options)
        return file_path

    monkeypatch.setattr(PYMUPDF4LLM_EXTRACT.pymupdf4llm, "to_markdown", fake_to_markdown)

    result = PYMUPDF4LLM_EXTRACT.extract_sync("document.pdf", ocr_enabled=ocr_enabled)

    assert result["content"] == "document.pdf"
    assert captured_options["use_ocr"] is ocr_enabled
