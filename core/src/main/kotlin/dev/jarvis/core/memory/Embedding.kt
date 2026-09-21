package dev.jarvis.core.memory

import dev.jarvis.core.util.Text
import kotlin.math.ln
import kotlin.math.sqrt

/**
 * Text embedding port.
 *
 * The core ships [HashingEmbedder]. A neural embedder (on-device model, or an online one the
 * user explicitly enabled) can be substituted without touching retrieval, because retrieval
 * only ever asks for a vector and a cosine.
 */
interface TextEmbedder {
    val dimensions: Int

    /** Surfaced in Settings and in capability reports, so the user knows what matching they got. */
    val description: String

    fun embed(text: String): FloatArray

    fun embedBatch(texts: List<String>): List<FloatArray> = texts.map { embed(it) }
}

fun cosineSimilarity(a: FloatArray, b: FloatArray): Double {
    if (a.size != b.size || a.isEmpty()) return 0.0
    var dot = 0.0
    var normA = 0.0
    var normB = 0.0
    for (i in a.indices) {
        val x = a[i].toDouble()
        val y = b[i].toDouble()
        dot += x * y
        normA += x * x
        normB += y * y
    }
    if (normA == 0.0 || normB == 0.0) return 0.0
    return dot / (sqrt(normA) * sqrt(normB))
}

/**
 * Deterministic, offline, dependency-free text embedding using the hashing trick.
 *
 * What it is: stemmed keywords and their bigrams are hashed into a fixed-width vector with
 * signed accumulation and sublinear term-frequency scaling, then L2-normalised. Two texts that
 * share vocabulary land close together; word order is largely ignored because bigrams carry it.
 *
 * What it is NOT, and this is stated in [description] because the user is told the truth:
 * it is not a neural embedding. It has no notion of meaning beyond shared morphology, so
 * "car" and "automobile" will not match. That gap is covered by the lexical half of the hybrid
 * retriever and by [Text.phraseSimilarity], and by a real embedder when one is installed.
 *
 * Hashing is FNV-1a rather than `String.hashCode` so vectors are reproducible across Kotlin
 * versions and JVMs - a stored index must not silently become garbage after an upgrade.
 */
class HashingEmbedder(
    override val dimensions: Int = 384,
    private val bigramWeight: Double = 0.55,
) : TextEmbedder {

    override val description: String =
        "On-device lexical embedding (${dimensions}d hashing, stemmed unigrams + bigrams). " +
            "No neural model, no network."

    override fun embed(text: String): FloatArray {
        val vector = FloatArray(dimensions)
        val tokens = Text.stems(Text.keywords(text))
        if (tokens.isEmpty()) {
            // Fall back to raw tokens so a query made entirely of stop words ("the a of")
            // still produces a usable vector instead of a zero vector that matches nothing.
            val raw = Text.tokenize(text)
            accumulate(vector, raw, 1.0)
        } else {
            accumulate(vector, tokens, 1.0)
            if (tokens.size >= 2) {
                val bigrams = (0 until tokens.size - 1).map { tokens[it] + "_" + tokens[it + 1] }
                accumulate(vector, bigrams, bigramWeight)
            }
        }
        l2Normalize(vector)
        return vector
    }

    private fun accumulate(vector: FloatArray, tokens: List<String>, weight: Double) {
        val counts = LinkedHashMap<String, Int>()
        for (token in tokens) counts[token] = (counts[token] ?: 0) + 1
        for ((token, count) in counts) {
            val hash = fnv1a(token)
            val index = Math.floorMod(hash, dimensions)
            // Signed hashing keeps the expected inner product unbiased when two different
            // tokens collide in the same bucket.
            val sign = if ((hash ushr 31) and 1 == 1) -1.0 else 1.0
            // Sublinear tf: the tenth occurrence of a word is not ten times the signal.
            val tf = 1.0 + ln(count.toDouble())
            vector[index] = (vector[index] + (sign * tf * weight)).toFloat()
        }
    }

    private fun l2Normalize(vector: FloatArray) {
        var sum = 0.0
        for (value in vector) sum += value.toDouble() * value
        val norm = sqrt(sum)
        if (norm > 0.0) {
            for (i in vector.indices) vector[i] = (vector[i] / norm).toFloat()
        }
    }

    companion object {
        /** FNV-1a, 32-bit. Chosen for stability across platforms and Kotlin versions. */
        fun fnv1a(input: String): Int {
            var hash = 0x811c9dc5.toInt()
            for (byte in input.encodeToByteArray()) {
                hash = hash xor (byte.toInt() and 0xff)
                // Multiply by the FNV prime 16777619 using shifts, to stay in Int range.
                hash += (hash shl 1) + (hash shl 4) + (hash shl 7) + (hash shl 8) + (hash shl 24)
            }
            return hash
        }
    }
}

/**
 * BM25 over an in-memory inverted index.
 *
 * The other half of hybrid retrieval. Lexical scoring is what saves the day when the embedding
 * is diluted by a long text, and it is exact for the cases users care about most: proper nouns,
 * app names, topic names and quoted phrases.
 */
class Bm25Index(
    private val k1: Double = 1.5,
    private val b: Double = 0.75,
) {
    private data class Posting(val documentId: String, val termFrequency: Int)

    private val postings = LinkedHashMap<String, MutableList<Posting>>()
    private val documentLengths = LinkedHashMap<String, Int>()
    private var totalLength = 0L

    val documentCount: Int get() = documentLengths.size

    private val averageLength: Double
        get() = if (documentLengths.isEmpty()) 0.0 else totalLength.toDouble() / documentLengths.size

    fun add(documentId: String, text: String) {
        remove(documentId)
        val terms = Text.stems(Text.keywords(text)).ifEmpty { Text.tokenize(text) }
        documentLengths[documentId] = terms.size
        totalLength += terms.size
        val counts = LinkedHashMap<String, Int>()
        for (term in terms) counts[term] = (counts[term] ?: 0) + 1
        for ((term, count) in counts) {
            postings.getOrPut(term) { mutableListOf() } += Posting(documentId, count)
        }
    }

    fun remove(documentId: String) {
        val length = documentLengths.remove(documentId) ?: return
        totalLength -= length
        val emptyTerms = mutableListOf<String>()
        for ((term, list) in postings) {
            list.removeAll { it.documentId == documentId }
            if (list.isEmpty()) emptyTerms += term
        }
        emptyTerms.forEach { postings.remove(it) }
    }

    fun clear() {
        postings.clear()
        documentLengths.clear()
        totalLength = 0
    }

    /** Scores every document against [queryText] and returns the top [limit], highest first. */
    fun search(queryText: String, limit: Int = 10): List<Scored> {
        val queryTerms = Text.stems(Text.keywords(queryText)).ifEmpty { Text.tokenize(queryText) }
        if (queryTerms.isEmpty() || documentLengths.isEmpty()) return emptyList()
        val avg = averageLength
        val scores = HashMap<String, Double>()

        for (term in queryTerms.toSet()) {
            val list = postings[term] ?: continue
            val df = list.size
            // Robertson-Sparck Jones idf, clamped so a term in every document contributes 0
            // rather than a small negative number.
            val idf = ln(1.0 + (documentCount - df + 0.5) / (df + 0.5)).coerceAtLeast(0.0)
            for (posting in list) {
                val length = documentLengths[posting.documentId] ?: continue
                val tf = posting.termFrequency.toDouble()
                val denominator = tf + k1 * (1.0 - b + b * (length / avg.coerceAtLeast(1.0)))
                val contribution = idf * (tf * (k1 + 1.0)) / denominator.coerceAtLeast(1e-9)
                scores[posting.documentId] = (scores[posting.documentId] ?: 0.0) + contribution
            }
        }

        return scores.entries
            .sortedByDescending { it.value }
            .take(limit)
            .map { Scored(it.key, it.value) }
    }

    /** Rebuilds from a set of documents, e.g. after a bulk import. */
    fun rebuild(documents: Map<String, String>) {
        clear()
        documents.forEach { (id, text) -> add(id, text) }
    }

    data class Scored(val documentId: String, val score: Double)
}
