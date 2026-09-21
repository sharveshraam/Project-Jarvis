package dev.jarvis.core.store

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.model.AutomationRule
import dev.jarvis.core.model.Conversation
import dev.jarvis.core.model.ConversationTurn
import dev.jarvis.core.model.KnowledgeChunk
import dev.jarvis.core.model.KnowledgeDocument
import dev.jarvis.core.model.LearningProfile
import dev.jarvis.core.model.MemoryCategory
import dev.jarvis.core.model.MemoryRecord
import dev.jarvis.core.model.Note
import dev.jarvis.core.model.ScheduledEvent
import dev.jarvis.core.model.TaskItem
import dev.jarvis.core.model.TeachingSession
import dev.jarvis.core.model.TopicProgress
import dev.jarvis.core.ports.FileStore
import dev.jarvis.core.ports.MemoryStore
import dev.jarvis.core.ports.SecretVault
import dev.jarvis.core.ports.ConfigStore
import dev.jarvis.core.ports.ConversationStore
import dev.jarvis.core.ports.AutomationStore
import dev.jarvis.core.ports.KnowledgeStore
import dev.jarvis.core.ports.LearningStore
import dev.jarvis.core.ports.NoteStore
import dev.jarvis.core.ports.ScheduleStore
import dev.jarvis.core.ports.TaskStore
import dev.jarvis.core.ports.VectorStore

/**
 * In-memory implementations of every storage port.
 *
 * Three reasons these live in `:core` rather than in a test source set:
 *
 *  1. They are what the entire offline behavioural suite runs against, which means memory,
 *     retrieval, automation, teaching and the agent loop are all tested end to end without a
 *     device or a database.
 *  2. They let `:app` degrade gracefully: if the SQLite store cannot be opened (corrupt file,
 *     full disk), the assistant still works for the session instead of crashing.
 *  3. They are the reference semantics. The SQLite adapters are written to match them, and a
 *     suite that passes against both is evidence they agree.
 *
 * None of these are thread-safe beyond simple synchronisation, because the app layer confines
 * storage access to a single background executor.
 */

class InMemoryConfigStore(initial: AssistantConfig? = null) : ConfigStore {
    private var current: AssistantConfig? = initial
    override fun load(): AssistantConfig = current ?: AssistantConfig.DEFAULT
    override fun save(config: AssistantConfig) { current = config }
    override fun isUninitialized(): Boolean = current == null
}

class InMemoryMemoryStore : MemoryStore {
    private val records = LinkedHashMap<String, MemoryRecord>()

    @Synchronized
    override fun upsert(record: MemoryRecord) { records[record.id] = record }

    @Synchronized
    override fun upsertAll(newRecords: List<MemoryRecord>) { newRecords.forEach { upsert(it) } }

    @Synchronized
    override fun findById(id: String): MemoryRecord? = records[id]

    @Synchronized
    override fun delete(id: String): Boolean = records.remove(id) != null

    @Synchronized
    override fun deleteTopic(topic: String): Int {
        val before = records.size
        records.entries.removeAll { it.value.topic.equals(topic, ignoreCase = true) }
        return before - records.size
    }

    @Synchronized
    override fun deleteByCategory(category: MemoryCategory): Int {
        val before = records.size
        records.entries.removeAll { it.value.category == category }
        return before - records.size
    }

    @Synchronized
    override fun all(): List<MemoryRecord> = records.values.toList()

    @Synchronized
    override fun count(): Int = records.size

    @Synchronized
    override fun clearAll(): Int = records.size.also { records.clear() }
}

class InMemoryVectorStore : VectorStore {
    private val vectors = LinkedHashMap<String, FloatArray>()

    @Synchronized
    override fun put(id: String, vector: FloatArray) { vectors[id] = vector }

    @Synchronized
    override fun get(id: String): FloatArray? = vectors[id]

    @Synchronized
    override fun remove(id: String) { vectors.remove(id) }

    @Synchronized
    override fun all(): Map<String, FloatArray> = LinkedHashMap(vectors)

    @Synchronized
    override fun clearAll(): Int = vectors.size.also { vectors.clear() }

    // Not @Synchronized: the annotation cannot target a property without a backing field, and
    // a read of a LinkedHashMap's size needs no lock here.
    override val size: Int get() = vectors.size
}

class InMemoryConversationStore : ConversationStore {
    private val conversations = LinkedHashMap<String, Conversation>()
    private val turns = LinkedHashMap<String, MutableList<ConversationTurn>>()
    private val order = mutableListOf<String>()

    @Synchronized
    override fun createConversation(conversation: Conversation) {
        conversations[conversation.id] = conversation
        turns.getOrPut(conversation.id) { mutableListOf() }
        if (conversation.id !in order) order += conversation.id
    }

    @Synchronized
    override fun deleteConversation(id: String): Boolean {
        order -= id
        turns.remove(id)
        return conversations.remove(id) != null
    }

    @Synchronized
    override fun conversation(id: String): Conversation? = conversations[id]?.let {
        it.copy(turns = turns[id].orEmpty().toList())
    }

    @Synchronized
    override fun appendTurn(turn: ConversationTurn) {
        createConversationIfMissing(turn.conversationId, turn.createdAt)
        turns.getOrPut(turn.conversationId) { mutableListOf() } += turn
        order.remove(turn.conversationId)
        order += turn.conversationId
        conversations[turn.conversationId]?.let {
            conversations[it.id] = it.copy(updatedAt = turn.createdAt)
        }
    }

    private fun createConversationIfMissing(id: String, atMillis: Long) {
        if (conversations[id] == null) {
            conversations[id] = Conversation(id, "Conversation", atMillis, atMillis)
            turns[id] = mutableListOf()
            order += id
        }
    }

    @Synchronized
    override fun turns(conversationId: String, limit: Int): List<ConversationTurn> =
        turns[conversationId].orEmpty().takeLast(limit)

    @Synchronized
    override fun recentTurns(limit: Int): List<ConversationTurn> =
        order.reversed().flatMap { turns[it].orEmpty() }.sortedBy { it.createdAt }.takeLast(limit)

    @Synchronized
    override fun conversations(): List<Conversation> =
        order.reversed().mapNotNull { conversations[it]?.copy(turns = turns[it].orEmpty().toList()) }

    @Synchronized
    override fun clearAll(): Int {
        val count = conversations.size
        conversations.clear(); turns.clear(); order.clear()
        return count
    }
}

class InMemoryTaskStore : TaskStore {
    private val tasks = LinkedHashMap<String, TaskItem>()

    @Synchronized override fun upsert(task: TaskItem) { tasks[task.id] = task }
    @Synchronized override fun delete(id: String): Boolean = tasks.remove(id) != null
    @Synchronized override fun find(id: String): TaskItem? = tasks[id]
    @Synchronized override fun all(): List<TaskItem> = tasks.values.toList()
    @Synchronized override fun open(): List<TaskItem> = tasks.values.filter { !it.isDone }
    @Synchronized
    override fun dueBetween(fromMillis: Long, toMillis: Long): List<TaskItem> =
        tasks.values.filter { it.dueAt != null && it.dueAt in fromMillis..toMillis }

    @Synchronized override fun clearAll(): Int = tasks.size.also { tasks.clear() }
}

class InMemoryScheduleStore : ScheduleStore {
    private val events = LinkedHashMap<String, ScheduledEvent>()

    @Synchronized override fun upsert(event: ScheduledEvent) { events[event.id] = event }
    @Synchronized override fun delete(id: String): Boolean = events.remove(id) != null
    @Synchronized
    override fun pending(nowMillis: Long): List<ScheduledEvent> =
        events.values.filter { it.fireAt >= nowMillis }.sortedBy { it.fireAt }

    @Synchronized override fun all(): List<ScheduledEvent> = events.values.sortedBy { it.fireAt }
    @Synchronized override fun clearAll(): Int = events.size.also { events.clear() }
}

class InMemoryNoteStore : NoteStore {
    private val notes = LinkedHashMap<String, Note>()

    @Synchronized override fun upsert(note: Note) { notes[note.id] = note }
    @Synchronized override fun delete(id: String): Boolean = notes.remove(id) != null
    @Synchronized override fun find(id: String): Note? = notes[id]
    @Synchronized override fun all(): List<Note> = notes.values.toList()
    @Synchronized override fun clearAll(): Int = notes.size.also { notes.clear() }
}

class InMemoryAutomationStore : AutomationStore {
    private val rules = LinkedHashMap<String, AutomationRule>()

    @Synchronized override fun upsert(rule: AutomationRule) { rules[rule.id] = rule }
    @Synchronized override fun delete(id: String): Boolean = rules.remove(id) != null
    @Synchronized override fun find(id: String): AutomationRule? = rules[id]
    @Synchronized override fun all(): List<AutomationRule> = rules.values.toList()
    @Synchronized override fun enabled(): List<AutomationRule> = rules.values.filter { it.enabled }

    @Synchronized
    override fun setEnabled(id: String, enabled: Boolean): Boolean {
        val rule = rules[id] ?: return false
        rules[id] = rule.copy(enabled = enabled)
        return true
    }

    @Synchronized
    override fun recordFire(id: String, atMillis: Long, error: String?) {
        val rule = rules[id] ?: return
        rules[id] = rule.copy(
            lastFiredAt = atMillis,
            fireCount = rule.fireCount + 1,
            lastError = error ?: rule.lastError,
        )
    }

    @Synchronized override fun clearAll(): Int = rules.size.also { rules.clear() }
}

class InMemoryLearningStore(initial: LearningProfile = LearningProfile()) : LearningStore {
    private var profile: LearningProfile = initial
    private val sessions = LinkedHashMap<String, TeachingSession>()

    @Synchronized override fun profile(): LearningProfile = profile
    @Synchronized override fun saveProfile(newProfile: LearningProfile) { profile = newProfile }

    @Synchronized
    override fun topicProgress(topicKey: String): TopicProgress? = profile.topics[topicKey]

    @Synchronized
    override fun saveTopicProgress(progress: TopicProgress) {
        profile = profile.copy(
            topics = profile.topics + (progress.topicKey to progress),
            updatedAt = progress.lastStudiedAt ?: profile.updatedAt,
        )
    }

    @Synchronized override fun sessions(): List<TeachingSession> = sessions.values.toList()

    @Synchronized
    override fun saveSession(session: TeachingSession) { sessions[session.id] = session }

    @Synchronized
    override fun clearAll(): Int {
        val count = sessions.size + profile.topics.size
        sessions.clear()
        profile = LearningProfile(profile.preferredStyle)
        return count
    }
}

class InMemoryKnowledgeStore : KnowledgeStore {
    private val documents = LinkedHashMap<String, KnowledgeDocument>()
    private val chunks = LinkedHashMap<String, MutableList<KnowledgeChunk>>()

    @Synchronized override fun upsertDocument(document: KnowledgeDocument) {
        documents[document.id] = document
        chunks.getOrPut(document.id) { mutableListOf() }
    }

    @Synchronized
    override fun deleteDocument(id: String): Boolean {
        chunks.remove(id)
        return documents.remove(id) != null
    }

    @Synchronized override fun document(id: String): KnowledgeDocument? = documents[id]
    @Synchronized override fun documents(): List<KnowledgeDocument> = documents.values.toList()

    @Synchronized
    override fun insertChunks(newChunks: List<KnowledgeChunk>) {
        for (chunk in newChunks) chunks.getOrPut(chunk.documentId) { mutableListOf() } += chunk
    }

    @Synchronized override fun chunks(documentId: String): List<KnowledgeChunk> =
        chunks[documentId].orEmpty().toList()

    @Synchronized override fun allChunks(): List<KnowledgeChunk> =
        chunks.values.flatten().sortedWith(compareBy({ it.documentId }, { it.ordinal }))

    @Synchronized override fun chunkCount(): Int = chunks.values.sumOf { it.size }

    @Synchronized
    override fun clearAll(): Int {
        val count = documents.size
        documents.clear(); chunks.clear()
        return count
    }
}

/**
 * In-memory secret vault.
 *
 * [isHardwareBacked] is false, and the app layer must never use this for a real secret: it
 * exists for tests and as a fallback that reports its own weakness. The Android adapter stores
 * the verifier in a file whose encryption key lives in the Keystore.
 */
class InMemorySecretVault : SecretVault {
    private val secrets = LinkedHashMap<String, ByteArray>()

    @Synchronized override fun store(key: String, secret: ByteArray) { secrets[key] = secret.copyOf() }
    @Synchronized override fun retrieve(key: String): ByteArray? = secrets[key]?.copyOf()
    @Synchronized override fun delete(key: String): Boolean = secrets.remove(key) != null
    @Synchronized override fun contains(key: String): Boolean = secrets.containsKey(key)
    override val isHardwareBacked: Boolean = false
}

/** In-memory file store, for tests and for export previews. */
class InMemoryFileStore : FileStore {
    private val files = LinkedHashMap<String, String>()

    private fun key(relativePath: String) = relativePath.trimStart('/')

    @Synchronized override fun writeText(relativePath: String, text: String): Boolean {
        files[key(relativePath)] = text
        return true
    }

    @Synchronized override fun readText(relativePath: String): String? = files[key(relativePath)]
    @Synchronized override fun delete(relativePath: String): Boolean = files.remove(key(relativePath)) != null
    @Synchronized override fun exists(relativePath: String): Boolean = files.containsKey(key(relativePath))

    @Synchronized
    override fun list(relativePath: String): List<String> {
        val prefix = key(relativePath).let { if (it.isEmpty()) "" else "$it/" }
        return files.keys.filter { it.startsWith(prefix) }.map { it.removePrefix(prefix) }
    }

    @Synchronized
    override fun sizeBytes(relativePath: String): Long =
        files[key(relativePath)]?.encodeToByteArray()?.size?.toLong() ?: 0L
}
