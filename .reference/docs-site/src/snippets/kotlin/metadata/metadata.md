```kotlin title="Kotlin"
import io.xberg.*

private const val DEFAULT_MAX_ARCHIVE_DEPTH = 3L
private const val DEFAULT_EXTRACTION_TIMEOUT_SECS = 600L
private const val DEFAULT_MAX_EMBEDDED_FILE_BYTES = 50L * 1024L * 1024L

fun main() {
    val config = ExtractionConfig(
        extractionTimeoutSecs = DEFAULT_EXTRACTION_TIMEOUT_SECS,
        maxEmbeddedFileBytes = DEFAULT_MAX_EMBEDDED_FILE_BYTES,
        url = UrlExtractionConfig(crawl = CrawlConfig(ssrf = SsrfPolicy())),
        maxArchiveDepth = DEFAULT_MAX_ARCHIVE_DEPTH,
    )
    val resultOutput = Xberg.extract(
        ExtractInput(kind = ExtractInputKind.URI, uri = "document.pdf"),
        config,
    )
    val result = resultOutput.results.first()

    val metadata = result.metadata
    metadata.title?.let { println("Title: $it") }
    metadata.authors?.let { println("Authors: ${it.joinToString(", ")}") }

    when (val format = metadata.format) {
        is FormatMetadata.Pdf -> {
            format.metadata.pageCount?.let { println("Pages: $it") }
            format.metadata.producer?.let { println("Producer: $it") }
            format.metadata.pdfVersion?.let { println("PDF Version: $it") }
        }
        else -> Unit
    }

    val htmlResultOutput = Xberg.extract(
        ExtractInput(kind = ExtractInputKind.URI, uri = "page.html"),
        config,
    )
    val htmlResult = htmlResultOutput.results.first()
    when (val format = htmlResult.metadata.format) {
        is FormatMetadata.Html -> {
            val html = format.metadata
            html.title?.let { println("Title: $it") }
            html.description?.let { println("Description: $it") }
            html.canonicalUrl?.let { println("Canonical URL: $it") }
            html.language?.let { println("Language: $it") }
            println("Keywords: ${html.keywords}")
            html.openGraph["image"]?.let { println("Open Graph Image: $it") }
            html.openGraph["title"]?.let { println("Open Graph Title: $it") }
            html.twitterCard["card"]?.let { println("Twitter Card Type: $it") }
            for (header in html.headers) {
                println("Header (level ${header.level}): ${header.text}")
            }
            for (link in html.links) {
                println("Link: ${link.href} (${link.text})")
            }
            for (image in html.images) {
                println("Image: ${image.src}")
            }
            if (html.structuredData.isNotEmpty()) {
                println("Structured data items: ${html.structuredData.size}")
            }
        }
        else -> Unit
    }
}
```
