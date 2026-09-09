```kotlin title="Kotlin"
import io.xberg.*

private const val DEFAULT_MAX_ARCHIVE_DEPTH = 3L
private const val DEFAULT_EXTRACTION_TIMEOUT_SECS = 600L
private const val DEFAULT_MAX_EMBEDDED_FILE_BYTES = 50L * 1024L * 1024L

fun main() {
    val inputs = listOf(
        ExtractInput(kind = ExtractInputKind.URI, uri = "report.pdf"),
        ExtractInput(kind = ExtractInputKind.URI, uri = "notes.txt"),
    )
    val config = ExtractionConfig(
        extractionTimeoutSecs = DEFAULT_EXTRACTION_TIMEOUT_SECS,
        maxEmbeddedFileBytes = DEFAULT_MAX_EMBEDDED_FILE_BYTES,
        url = UrlExtractionConfig(crawl = CrawlConfig(ssrf = SsrfPolicy())),
        maxArchiveDepth = DEFAULT_MAX_ARCHIVE_DEPTH,
    )

    val output = Xberg.extractBatch(inputs, config)
    output.results.forEach { println(it.content) }
}
```
