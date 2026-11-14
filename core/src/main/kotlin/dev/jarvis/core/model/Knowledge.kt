package dev.jarvis.core.model

/**
 * Knowledge base model.
 *
 * The user supplies PDFs, notes, documents, images and (when online) saved web pages. The
 * text is extracted on the device, chunked, indexed and retrieved locally. Retrieval never
 * requires connectivity once ingestion is done.
 */

enum class SourceKind(val label: String) {
    PDF("PDF"),
    PLAIN_TEXT("Text file"),
    NOTE("Note you wrote"),
    IMAGE("Image"),
    WEB_PAGE("Saved web page"),
    MANUAL("Typed in by hand"),
}

/**
 * How text was obtained from a source. Recorded because extraction quality varies hugely and
 * the assistant must be honest about it: a scanned PDF with no text layer yields nothing
 * usable, and saying "I could not read that page" is better than inventing an answer.
 */
enum class ExtractionQuality(val label: String) {
    /** A real text layer, or a plain text file. Reliable. */
    TEXT_LAYER("Direct text"),

    /** Extracted from a document's structure (headings, lists) as well as its text. */
    STRUCTURED("Structured text"),

    /** Rendered pages with no text layer; needs OCR, which is NOT bundled. */
    IMAGE_ONLY("Image only - text not extractable"),

    /** Extraction failed or produced nothing usable. */
    FAILED("Could not extract text"),
}

data class KnowledgeDocument(
    val id: String,
    val title: String,
    val sourceKind: SourceKind,
    /** Content URI or file path. Never leaves the device. */
    val location: String,
    val addedAt: Long,
    val charCount: Int = 0,
    val chunkCount: Int = 0,
    val pageCount: Int? = null,
    val quality: ExtractionQuality = ExtractionQuality.TEXT_LAYER,
    val checksum: String? = null,
    val tags: List<String> = emptyList(),
    /** Set when the user approved caching online material for offline use. */
    val cachedFromOnline: Boolean = false,
    val originalUrl: String? = null,
)

/**
 * A chunk of a document. Chunks carry their heading path so an answer can be attributed:
 * "from section 3.2 of your circuits notes".
 */
data class KnowledgeChunk(
    val id: String,
    val documentId: String,
    val ordinal: Int,
    val text: String,
    val headingPath: List<String> = emptyList(),
    val page: Int? = null,
    val charCount: Int = text.length,
)

/** A retrieval hit, with the score that produced it so ranking can be explained. */
data class RetrievedChunk(
    val chunk: KnowledgeChunk,
    val documentTitle: String,
    val score: Double,
    val lexicalScore: Double,
    val semanticScore: Double,
) {
    /** Human-readable attribution, e.g. "Circuits notes, section 3.2, page 41". */
    fun attribution(): String = buildString {
        append(documentTitle)
        val heading = chunk.headingPath.lastOrNull()
        if (!heading.isNullOrBlank()) append(", ").append(heading)
        chunk.page?.let { append(", page ").append(it) }
    }
}
