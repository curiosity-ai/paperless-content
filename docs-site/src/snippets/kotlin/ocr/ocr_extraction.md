```kotlin title="Kotlin"
import io.xberg.*

private const val DEFAULT_MAX_ARCHIVE_DEPTH = 3L
private const val DEFAULT_EXTRACTION_TIMEOUT_SECS = 600L
private const val DEFAULT_MAX_EMBEDDED_FILE_BYTES = 50L * 1024L * 1024L

fun main() {
    val ocr = OcrConfig(backend = "tesseract", language = listOf("eng"))
    val config = ExtractionConfig(
        ocr = ocr,
        extractionTimeoutSecs = DEFAULT_EXTRACTION_TIMEOUT_SECS,
        maxEmbeddedFileBytes = DEFAULT_MAX_EMBEDDED_FILE_BYTES,
        url = UrlExtractionConfig(crawl = CrawlConfig(ssrf = SsrfPolicy())),
        maxArchiveDepth = DEFAULT_MAX_ARCHIVE_DEPTH,
    )

    val resultOutput = Xberg.extract(
        ExtractInput(kind = ExtractInputKind.URI, uri = "scanned.pdf"),
        config,
    )
    val result = resultOutput.results.first()
    println(result.content)
}
```
