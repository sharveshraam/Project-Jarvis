package dev.jarvis.core.memory

import dev.jarvis.core.model.MemoryCategory
import dev.jarvis.core.model.MemoryRecord
import dev.jarvis.core.model.MemorySource
import dev.jarvis.core.model.PROFILE_CATEGORIES
import dev.jarvis.core.ports.MemoryStore
import dev.jarvis.core.ports.VectorStore
import dev.jarvis.core.util.ClockPort
import dev.jarvis.core.util.IdPort
import dev.jarvis.core.util.JsonArray
import dev.jarvis.core.util.JsonNumber
import dev.jarvis.core.util.JsonObject
import dev.jarvis.core.util.Text
import dev.jarvis.core.util.json
import dev.jarvis.core.util.jsonArray
import dev.jarvis.core.util.parseJsonOrNull
import kotlin.math.exp
import kotlin.math.min

/** One retrieval result, with the scores that produced it so ranking can be explained. */
data class MemoryHit(
    val record: MemoryRecord,
    val score: Double,
    val semanticScore: Double,
    val lexicalScore: Double,
    /** Human-readable reason this surfaced, e.g. "topic match: robotics project". */
    val reason: String,
)

data class ForgetResult(
    val deleted: Int,
    val deletedTexts: List<String>,
    /** What was actually matched, so the UI can confirm before/after. */
    val query: String,
) {
    val nothingDeleted: Boolean get() = deleted == 0
}

/**
 * Structured personal memory.
 *
 * This is the component that makes the assistant personal rather than stateless, and the
 * requirement is explicit that it must NOT be "the whole chat history". So:
 *
 *  * memories are typed by [MemoryCategory] and carry provenance ([MemorySource]);
 *  * temporary context and permanent memory are different things, and only temporary context
 *    expires;
 *  * retrieval is hybrid - vector cosine from [HashingEmbedder] plus BM25 - plus importance,
 *    recency and topic-match boosts, because any one of those signals alone ranks badly;
 *  * near-duplicate saves are merged rather than accumulated, so telling the assistant the same
 *    thing five times does not produce five memories;
 *  * forgetting works by id, by topic phrase and in bulk, and reports exactly what it removed.
 *
 * Nothing here touches the network. There is no code path by which a memory leaves the device
 * except through an explicit user-initiated export.
 */
class MemoryManager(
    private val store: MemoryStore,
    private val vectors: VectorStore,
    private val clock: ClockPort,
    private val ids: IdPort,
    private val embedder: TextEmbedder = HashingEmbedder(),
    private val lexical: Bm25Index = Bm25Index(),
    private val config: () -> Boolean = { true },
) {

    init {
        rebuildIndex()
    }

    /** Whether memory is switched on. When off, nothing is written and nothing is recalled. */
    val isEnabled: Boolean get() = config()

    companion object {
        /** How long temporary conversation context lives before pruning, in millis. */
        const val TEMPORARY_TTL_MILLIS = 6L * 60 * 60 * 1000

        /** Below this combined score a memory is not considered relevant enough to surface. */
        const val MIN_RECALL_SCORE = 0.06

        /** Above this similarity, a new save updates the existing memory instead of adding one. */
        const val DEDUPE_THRESHOLD = 0.90

        private val DEFAULT_IMPORTANCE = mapOf(
            MemoryCategory.USER_PROFILE to 0.85,
            MemoryCategory.PREFERENCE to 0.75,
            MemoryCategory.LONG_TERM to 0.70,
            MemoryCategory.TASK to 0.55,
            MemoryCategory.LEARNING to 0.60,
            MemoryCategory.KNOWLEDGE to 0.60,
            MemoryCategory.CONVERSATION_CONTEXT to 0.25,
        )
    }

    // --------------------------------------------------------------------- writing ----

    /**
     * Stores a memory.
     *
     * When [dedupe] is on and a near-identical memory already exists in the same category, that
     * record is refreshed (importance nudged up, timestamp updated, attributes merged) rather
     * than duplicated. The returned record is the one that is actually stored.
     */
    fun remember(
        text: String,
        category: MemoryCategory = MemoryCategory.LONG_TERM,
        topic: String? = null,
        tags: List<String> = emptyList(),
        importance: Double? = null,
        permanent: Boolean = category != MemoryCategory.CONVERSATION_CONTEXT,
        source: MemorySource = MemorySource.USER_STATED,
        pinned: Boolean = false,
        attributes: Map<String, String> = emptyMap(),
        dedupe: Boolean = true,
        ttlMillis: Long? = if (permanent) null else TEMPORARY_TTL_MILLIS,
    ): MemoryRecord? {
        val cleaned = text.trim()
        if (cleaned.isEmpty() || !isEnabled) return null

        val now = clock.nowMillis()
        val resolvedTopic = topic?.takeIf { it.isNotBlank() } ?: TopicKey.of(cleaned).takeIf { it.isNotEmpty() }
        val resolvedImportance = importance ?: DEFAULT_IMPORTANCE[category] ?: 0.5

        if (dedupe) {
            val existing = findDuplicate(cleaned, category)
            if (existing != null) {
                val merged = existing.copy(
                    text = cleaned,
                    topic = resolvedTopic ?: existing.topic,
                    tags = (existing.tags + tags).distinct(),
                    importance = min(1.0, maxOf(existing.importance, resolvedImportance) + 0.05),
                    updatedAt = now,
                    permanent = permanent || existing.permanent,
                    pinned = pinned || existing.pinned,
                    attributes = existing.attributes + attributes,
                )
                persist(merged)
                return merged
            }
        }

        val record = MemoryRecord(
            id = ids.newId("mem"),
            category = category,
            text = cleaned,
            topic = resolvedTopic,
            tags = tags.map { Text.normalize(it) }.filter { it.isNotBlank() }.distinct(),
            source = source,
            createdAt = now,
            updatedAt = now,
            importance = resolvedImportance,
            pinned = pinned,
            permanent = permanent,
            expiresAt = ttlMillis?.let { now + it },
            attributes = attributes,
        )
        persist(record)
        return record
    }

    private fun findDuplicate(text: String, category: MemoryCategory): MemoryRecord? {
        var best: MemoryRecord? = null
        var bestScore = 0.0
        for (record in store.all()) {
            if (record.category != category) continue
            if (record.text.equals(text, ignoreCase = true)) return record
            val score = Text.phraseSimilarity(text, record.text)
            if (score > bestScore) { bestScore = score; best = record }
        }
        return if (bestScore >= DEDUPE_THRESHOLD) best else null
    }

    private fun persist(record: MemoryRecord) {
        store.upsert(record)
        vectors.put(record.id, embedder.embed(record.text))
        lexical.add(record.id, record.text)
    }

    /** Rebuilds both indexes from the store. Called after an import or a store swap. */
    fun rebuildIndex() {
        lexical.clear()
        for (record in store.all()) {
            vectors.put(record.id, embedder.embed(record.text))
            lexical.add(record.id, record.text)
        }
    }

    // -------------------------------------------------------------------- retrieval ----

    /**
     * Hybrid recall.
     *
     * Vector similarity handles paraphrase and word order, BM25 handles exact terms (names,
     * app names, topic words), and the boosts encode what actually matters for a personal
     * assistant: a pinned user-profile fact outranks yesterday's throwaway context even when the
     * throwaway context is lexically closer.
     */
    fun recall(
        query: String,
        limit: Int = 5,
        categories: Set<MemoryCategory>? = null,
        nowMillis: Long = clock.nowMillis(),
        includeExpired: Boolean = false,
    ): List<MemoryHit> {
        if (!isEnabled || query.isBlank()) return emptyList()

        val candidates = store.all().filter { record ->
            if (categories != null && record.category !in categories) return@filter false
            if (!includeExpired && record.isExpiredAt(nowMillis)) return@filter false
            true
        }
        if (candidates.isEmpty()) return emptyList()

        val queryVector = embedder.embed(query)
        val lexicalScores = lexical.search(query, limit = candidates.size + 10)
            .associate { it.documentId to it.score }
        val maxLexical = lexicalScores.values.maxOrNull() ?: 0.0
        val queryTopic = TopicKey.of(query)

        val hits = candidates.mapNotNull { record ->
            val vector = vectors.get(record.id) ?: embedder.embed(record.text)
            val semantic = cosineSimilarity(queryVector, vector).coerceIn(0.0, 1.0)
            val rawLexical = lexicalScores[record.id] ?: 0.0
            val lexicalNorm = if (maxLexical > 0.0) rawLexical / maxLexical else 0.0

            var combined = 0.55 * semantic + 0.45 * lexicalNorm
            val reasons = mutableListOf<String>()
            var topicMatched = false
            var directMention = false
            var tagMatched = false

            // Topic match: "what do you remember about my robotics project" should find a
            // memory whose topic key is the robotics project even if the wording differs.
            val topicKey = record.topic
            if (!topicKey.isNullOrBlank() && queryTopic.isNotBlank()) {
                val topicScore = TopicKey.matchScore(query, topicKey)
                if (topicScore >= 0.55) {
                    combined += 0.30 * topicScore
                    reasons += "topic match: $topicKey"
                    topicMatched = true
                }
            }

            // Substring containment, in either direction, is strong evidence.
            if (Text.fuzzyContains(record.text, query, threshold = 0.8) ||
                Text.fuzzyContains(query, record.text, threshold = 0.8)
            ) {
                combined += 0.25
                reasons += "direct mention"
                directMention = true
            }

            for (tag in record.tags) {
                if (Text.fuzzyContains(query, tag, threshold = 0.8)) {
                    combined += 0.10
                    reasons += "tagged '$tag'"
                    tagMatched = true
                    break
                }
            }

            // Relevance gate. Importance, recency and pinned status are ranking refinements:
            // they must never create relevance out of nothing, or a pinned memory would be
            // injected into the context of every unrelated question. Without this gate an
            // empty-lexical query still scored ~0.15 purely from boosts.
            val hasSignal = semantic >= 0.05 || lexicalNorm >= 0.05 ||
                topicMatched || directMention || tagMatched
            if (!hasSignal) return@mapNotNull null

            // Importance, recency and how often this has been useful before.
            combined += 0.10 * record.importance
            val ageDays = (nowMillis - record.updatedAt).toDouble() / 86_400_000.0
            val recency = if (record.permanent) exp(-ageDays / 180.0) else exp(-ageDays / 7.0)
            combined += 0.08 * recency
            combined += 0.04 * (min(record.recallCount, 10) / 10.0)
            if (record.pinned) {
                combined += 0.15
                reasons += "pinned"
            }
            if (reasons.isEmpty()) {
                reasons += if (semantic >= lexicalNorm) "similar wording" else "shared terms"
            }

            if (combined < MIN_RECALL_SCORE) null
            else MemoryHit(
                record = record,
                score = combined,
                semanticScore = semantic,
                lexicalScore = lexicalNorm,
                reason = reasons.joinToString(", "),
            )
        }

        return hits.sortedByDescending { it.score }.take(limit)
    }

    /**
     * Formats recalled memories as a context block for a provider prompt.
     *
     * Each line is attributed so an answer can cite which memory it used, and the whole block is
     * capped at [maxChars] so a long memory cannot crowd out the user's actual question.
     */
    fun contextBlock(query: String, limit: Int = 6, maxChars: Int = 1400): Pair<String, List<String>> {
        val hits = recall(query, limit)
        if (hits.isEmpty()) return "" to emptyList()
        val lines = mutableListOf<String>()
        val idsUsed = mutableListOf<String>()
        var used = 0
        for (hit in hits) {
            val line = "- [${hit.record.category.label}] ${hit.record.text}"
            if (used + line.length > maxChars) break
            lines += line
            idsUsed += hit.record.id
            used += line.length + 1
        }
        return lines.joinToString("\n") to idsUsed
    }

    /** Records that a memory was surfaced, so recency and usefulness feed future ranking. */
    fun markRecalled(ids: List<String>, nowMillis: Long = clock.nowMillis()) {
        for (id in ids) {
            store.findById(id)?.let { persist(it.withRecall(nowMillis)) }
        }
    }

    // --------------------------------------------------------------------- forgetting ----

    fun forget(id: String): Boolean {
        lexical.remove(id)
        vectors.remove(id)
        return store.delete(id)
    }

    /**
     * "Forget what I told you about X."
     *
     * Matches the user's phrasing against topic keys first and memory text second, and returns
     * exactly what was removed. Deleting nothing is a legitimate outcome and is reported as
     * such - the assistant must not claim to have forgotten something it never had.
     */
    fun forgetMatching(query: String, threshold: Double = 0.5): ForgetResult {
        // Reduce the request to its subject first. "Forget what I told you about the robotics
        // project" must match a memory whose topic is the robotics project; scoring the whole
        // sentence, meta-words included, would fall below the threshold and silently delete
        // nothing - the worst failure mode for a privacy control.
        val normalizedQuery = TopicKey.stripForgetPrefix(query)
        val effectiveQuery = normalizedQuery.ifBlank { query }

        val victims = store.all().filter { record ->
            val topicScore = record.topic?.let { TopicKey.matchScore(effectiveQuery, it) } ?: 0.0
            val textScore = Text.phraseSimilarity(effectiveQuery, record.text)
            maxOf(topicScore, textScore) >= threshold
        }

        for (record in victims) forget(record.id)
        return ForgetResult(
            deleted = victims.size,
            deletedTexts = victims.map { it.text },
            query = effectiveQuery,
        )
    }

    fun forgetCategory(category: MemoryCategory): Int {
        val ids = store.all().filter { it.category == category }.map { it.id }
        ids.forEach { forget(it) }
        return ids.size
    }

    /** Removes temporary context only. Long-term memory is untouched. */
    fun forgetTemporary(): Int = forgetCategory(MemoryCategory.CONVERSATION_CONTEXT)

    fun forgetAll(): Int {
        val count = store.clearAll()
        lexical.clear()
        vectors.clearAll()
        return count
    }

    /** Deletes temporary context that has aged out. Returns how many were removed. */
    fun pruneExpired(nowMillis: Long = clock.nowMillis()): Int {
        val expired = store.all().filter { it.isExpiredAt(nowMillis) }
        expired.forEach { forget(it.id) }
        return expired.size
    }

    // ------------------------------------------------------------------- introspection ----

    val count: Int get() = store.count()

    fun all(): List<MemoryRecord> = store.all()

    fun byCategory(category: MemoryCategory): List<MemoryRecord> =
        store.all().filter { it.category == category }

    /**
     * Answers "what do you remember about me?".
     *
     * Grouped by category, most important first, and honest about being empty: a fresh install
     * says so plainly instead of inventing a profile.
     */
    fun profileSummary(nickname: String = "Jarvis"): String {
        if (!isEnabled) {
            return "Memory is turned off, so $nickname is not storing anything about you. " +
                "You can turn it back on in Settings > Privacy."
        }
        val relevant = store.all()
            .filter { it.category in PROFILE_CATEGORIES }
            .sortedWith(compareByDescending<MemoryRecord> { it.pinned }.thenByDescending { it.importance })

        if (relevant.isEmpty()) {
            return "Nothing yet. $nickname has no stored profile, preferences or facts about " +
                "you. Tell me something to remember and it will be kept on this device."
        }

        val grouped = relevant.groupBy { it.category }
        val sb = StringBuilder()
        sb.append("Here is what I remember about you (").append(relevant.size).append(" item")
            .append(if (relevant.size == 1) "" else "s").append(", all stored locally):\n")
        for (category in listOf(MemoryCategory.USER_PROFILE, MemoryCategory.PREFERENCE, MemoryCategory.LONG_TERM)) {
            val items = grouped[category] ?: continue
            sb.append('\n').append(category.label.uppercase()).append('\n')
            items.forEachIndexed { index, record ->
                sb.append("  ").append(index + 1).append(". ").append(record.text)
                if (record.pinned) sb.append("  [pinned]")
                sb.append('\n')
            }
        }
        val others = relevant.size - grouped.values.sumOf { it.size }
        if (others > 0) sb.append("\n(plus $others more in other categories)\n")
        sb.append("\nSay \"forget\" followed by any of these to remove it.")
        return sb.toString().trimEnd()
    }

    // ------------------------------------------------------------------ export/import ----

    /** Exports every memory as JSON. Used by the user-initiated export control. */
    fun exportJson(): String = JsonArray(store.all().map { recordToJson(it) }).encode()

    private fun recordToJson(record: MemoryRecord): JsonObject = JsonObject(
        linkedMapOf(
            "id" to json(record.id),
            "category" to json(record.category.name),
            "text" to json(record.text),
            "topic" to json(record.topic),
            "tags" to jsonArray(*record.tags.map { json(it) }.toTypedArray()),
            "source" to json(record.source.name),
            "createdAt" to json(record.createdAt),
            "updatedAt" to json(record.updatedAt),
            "lastRecalledAt" to json(record.lastRecalledAt),
            "recallCount" to json(record.recallCount),
            "importance" to json(record.importance),
            "pinned" to json(record.pinned),
            "permanent" to json(record.permanent),
            "expiresAt" to json(record.expiresAt),
            "attributes" to JsonObject(record.attributes.mapValues { json(it.value) }),
        ),
    )

    /**
     * Imports memories from a previous export.
     *
     * Ids are regenerated, so importing the same file twice creates duplicates rather than
     * colliding - a deliberate choice, because silently overwriting an existing memory on
     * import would be a way to lose data.
     */
    fun importJson(text: String, source: MemorySource = MemorySource.IMPORTED): Int {
        val root = parseJsonOrNull(text) as? JsonArray ?: return 0
        val now = clock.nowMillis()
        var imported = 0
        for (item in root.items) {
            val obj = item as? JsonObject ?: continue
            val recordText = obj.string("text") ?: continue
            val category = MemoryCategory.entries.firstOrNull {
                it.name == obj.string("category")
            } ?: MemoryCategory.LONG_TERM
            val recordSource = MemorySource.entries.firstOrNull {
                it.name == obj.string("source")
            } ?: source
            val record = MemoryRecord(
                id = ids.newId("mem"),
                category = category,
                text = recordText,
                topic = obj.string("topic"),
                tags = obj.stringList("tags"),
                source = recordSource,
                createdAt = (obj["createdAt"] as? JsonNumber)?.value?.toLong() ?: now,
                updatedAt = now,
                importance = obj.double("importance") ?: 0.5,
                pinned = obj.bool("pinned") ?: false,
                permanent = obj.bool("permanent") ?: true,
                attributes = obj.obj("attributes")?.entries
                    ?.mapNotNull { (key, value) -> (value as? dev.jarvis.core.util.JsonString)?.let { key to it.value } }
                    ?.toMap() ?: emptyMap(),
            )
            persist(record)
            imported++
        }
        return imported
    }
}
