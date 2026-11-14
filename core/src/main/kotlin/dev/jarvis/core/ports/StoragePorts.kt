package dev.jarvis.core.ports

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.model.AutomationRule
import dev.jarvis.core.model.Conversation
import dev.jarvis.core.model.ConversationTurn
import dev.jarvis.core.model.KnowledgeChunk
import dev.jarvis.core.model.KnowledgeDocument
import dev.jarvis.core.model.LearningProfile
import dev.jarvis.core.model.MemoryRecord
import dev.jarvis.core.model.Note
import dev.jarvis.core.model.ScheduledEvent
import dev.jarvis.core.model.TaskItem
import dev.jarvis.core.model.TeachingSession
import dev.jarvis.core.model.TopicProgress

/**
 * Persistence ports.
 *
 * Every store is an interface for one reason the requirements state explicitly: the database
 * must be replaceable. `:core` ships an in-memory implementation of each, which is what the
 * offline test suite runs against; `:app` supplies SQLite-backed implementations. Swapping in
 * Room, or a file-backed store, or an encrypted store is a change confined to `:app`.
 *
 * Two rules apply to all of them:
 *  * Everything is local. None of these ports has a network path.
 *  * Everything is deletable, individually and in bulk, because the user must be able to
 *    delete any memory, any conversation, any document, or everything.
 */

/** Assistant configuration. Small, read on every request, written rarely. */
interface ConfigStore {
    fun load(): AssistantConfig
    fun save(config: AssistantConfig)
    /** True when no configuration has ever been saved, i.e. this is a first run. */
    fun isUninitialized(): Boolean
}

/** Long-term and short-term memory. */
interface MemoryStore {
    fun upsert(record: MemoryRecord)
    fun upsertAll(records: List<MemoryRecord>)
    fun findById(id: String): MemoryRecord?
    fun delete(id: String): Boolean
    fun deleteTopic(topic: String): Int
    fun deleteByCategory(category: dev.jarvis.core.model.MemoryCategory): Int
    fun all(): List<MemoryRecord>
    fun count(): Int
    fun clearAll(): Int
}

/** Conversations and their turns. */
interface ConversationStore {
    fun createConversation(conversation: Conversation)
    fun deleteConversation(id: String): Boolean
    fun conversation(id: String): Conversation?
    fun appendTurn(turn: ConversationTurn)
    fun turns(conversationId: String, limit: Int = Int.MAX_VALUE): List<ConversationTurn>
    fun recentTurns(limit: Int): List<ConversationTurn>
    fun conversations(): List<Conversation>
    fun clearAll(): Int
}

/** Tasks, reminders and the armed schedule entries. */
interface TaskStore {
    fun upsert(task: TaskItem)
    fun delete(id: String): Boolean
    fun find(id: String): TaskItem?
    fun all(): List<TaskItem>
    fun open(): List<TaskItem>
    fun dueBetween(fromMillis: Long, toMillis: Long): List<TaskItem>
    fun clearAll(): Int
}

interface ScheduleStore {
    fun upsert(event: ScheduledEvent)
    fun delete(id: String): Boolean
    fun pending(nowMillis: Long): List<ScheduledEvent>
    fun all(): List<ScheduledEvent>
    fun clearAll(): Int
}

interface NoteStore {
    fun upsert(note: Note)
    fun delete(id: String): Boolean
    fun find(id: String): Note?
    fun all(): List<Note>
    fun clearAll(): Int
}

/** Automation rules. The user must be able to inspect, edit, disable and delete every one. */
interface AutomationStore {
    fun upsert(rule: AutomationRule)
    fun delete(id: String): Boolean
    fun find(id: String): AutomationRule?
    fun all(): List<AutomationRule>
    fun enabled(): List<AutomationRule>
    fun setEnabled(id: String, enabled: Boolean): Boolean
    fun recordFire(id: String, atMillis: Long, error: String?)
    fun clearAll(): Int
}

/** Learning profile and topic progress. */
interface LearningStore {
    fun profile(): LearningProfile
    fun saveProfile(profile: LearningProfile)
    fun topicProgress(topicKey: String): TopicProgress?
    fun saveTopicProgress(progress: TopicProgress)
    fun sessions(): List<TeachingSession>
    fun saveSession(session: TeachingSession)
    fun clearAll(): Int
}

/**
 * Knowledge base: documents the user supplied, and their chunks.
 *
 * Embeddings are stored as parallel arrays keyed by chunk id, in a separate port, so a store
 * implementation does not have to support binary blobs.
 */
interface KnowledgeStore {
    fun upsertDocument(document: KnowledgeDocument)
    fun deleteDocument(id: String): Boolean
    fun document(id: String): KnowledgeDocument?
    fun documents(): List<KnowledgeDocument>
    fun insertChunks(chunks: List<KnowledgeChunk>)
    fun chunks(documentId: String): List<KnowledgeChunk>
    fun allChunks(): List<KnowledgeChunk>
    fun chunkCount(): Int
    fun clearAll(): Int
}

/**
 * Vector storage for retrieval indexes.
 *
 * The core supplies a hashing embedder; this port lets a real neural embedder's vectors be
 * stored the same way. Vectors are plain float arrays, so any store can hold them.
 */
interface VectorStore {
    fun put(id: String, vector: FloatArray)
    fun get(id: String): FloatArray?
    fun remove(id: String)
    fun all(): Map<String, FloatArray>
    fun clearAll(): Int
    val size: Int
}

/**
 * Secret material.
 *
 * On Android this is backed by the Keystore and by a file that is never world-readable. The
 * port deliberately deals in byte arrays rather than Strings so callers cannot accidentally
 * persist a secret into a log or a JSON blob.
 */
interface SecretVault {
    fun store(key: String, secret: ByteArray)
    fun retrieve(key: String): ByteArray?
    fun delete(key: String): Boolean
    fun contains(key: String): Boolean
    /** True when the backing store is hardware-protected (Android Keystore). */
    val isHardwareBacked: Boolean
}

/**
 * Files, for exports and ingested documents.
 *
 * Scoped to the app's own storage: the assistant never reads arbitrary paths, it reads a URI
 * the user picked.
 */
interface FileStore {
    fun writeText(relativePath: String, text: String): Boolean
    fun readText(relativePath: String): String?
    fun delete(relativePath: String): Boolean
    fun exists(relativePath: String): Boolean
    fun list(relativePath: String): List<String>
    fun sizeBytes(relativePath: String): Long
}
