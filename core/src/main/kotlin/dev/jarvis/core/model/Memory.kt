package dev.jarvis.core.model

/**
 * Structured long-term memory.
 *
 * The requirement is explicit that memory must NOT simply mean "the whole chat history".
 * So a memory is a typed record with a category, a topic key, a provenance, an importance
 * and a lifetime flag - which is what makes "what do you remember about me?", "forget what
 * I told you about X" and "don't remember this" implementable rather than aspirational.
 */
enum class MemoryCategory(val label: String) {
    /** Who the user is: name, preferences, communication style, frequent apps. */
    USER_PROFILE("User profile"),

    /** How the user wants the assistant to behave: response style, learning style, schedules. */
    PREFERENCE("Preferences"),

    /** Facts the user explicitly asked to be remembered, ongoing projects, goals. */
    LONG_TERM("Long-term memory"),

    /** Tasks, reminders, deadlines, projects. */
    TASK("Tasks and reminders"),

    /** Recent conversation, current task, current app, current screen. Temporary by nature. */
    CONVERSATION_CONTEXT("Current context"),

    /** What the user has learned and where they struggled, from teaching mode. */
    LEARNING("Learning progress"),

    /** Content the user supplied: notes, PDFs, saved articles. */
    KNOWLEDGE("Knowledge base"),
}

/** Where a memory came from. Inferred memories are never treated as authoritative. */
enum class MemorySource {
    /** The user said it, e.g. "remember that I'm working on an ECE robotics project". */
    USER_STATED,

    /** Derived by the assistant from behaviour; always lower confidence, always deletable. */
    INFERRED,

    /** Produced by teaching mode. */
    TEACHING,

    /** Produced by an automation rule. */
    AUTOMATION,

    /** Loaded from a user export or an ingested document. */
    IMPORTED,
}

/**
 * One unit of memory.
 *
 * [permanent] is the distinction the requirements ask for between temporary context and
 * permanent memory: conversation context expires and is pruned, long-term memory is not.
 *
 * [topic] is a normalised key ("ece robotics project" -> "ece robotics project") that makes
 * "forget what I told you about X" a lookup rather than a guess.
 */
data class MemoryRecord(
    val id: String,
    val category: MemoryCategory,
    val text: String,
    val topic: String? = null,
    val tags: List<String> = emptyList(),
    val source: MemorySource = MemorySource.USER_STATED,
    val createdAt: Long = 0L,
    val updatedAt: Long = 0L,
    val lastRecalledAt: Long? = null,
    val recallCount: Int = 0,
    /** 0..1. Affects ranking and pruning order. */
    val importance: Double = 0.5,
    /** Pinned memories survive pruning and "clear context". */
    val pinned: Boolean = false,
    /** False means this is temporary context and may be pruned. */
    val permanent: Boolean = true,
    /** Optional expiry for temporary context, in epoch millis. */
    val expiresAt: Long? = null,
    /** Free-form structured detail, e.g. deadline, app package, contact name. */
    val attributes: Map<String, String> = emptyMap(),
) {
    /**
     * Whether temporary context has aged out.
     *
     * Takes [now] explicitly rather than reading a clock, so expiry is testable and the same
     * record cannot be "expired" in one call site and alive in another.
     */
    fun isExpiredAt(nowMillis: Long): Boolean =
        !permanent && !pinned && expiresAt != null && expiresAt <= nowMillis

    fun attribute(key: String): String? = attributes[key]

    fun withRecall(atMillis: Long): MemoryRecord =
        copy(lastRecalledAt = atMillis, recallCount = recallCount + 1)
}

/** Categories that hold temporary context rather than durable facts. */
val TEMPORARY_CATEGORIES = setOf(MemoryCategory.CONVERSATION_CONTEXT)

/** Categories surfaced by "what do you remember about me?". */
val PROFILE_CATEGORIES = setOf(
    MemoryCategory.USER_PROFILE,
    MemoryCategory.PREFERENCE,
    MemoryCategory.LONG_TERM,
)
