package dev.jarvis.core.model

/** Who produced a turn in a conversation. */
enum class Speaker {
    USER,
    ASSISTANT,
    /** A turn synthesised by the system, e.g. an automation result or a confirmation prompt. */
    SYSTEM,
}

/**
 * Where a turn was produced. Every assistant turn carries this, because the requirement is
 * that LOCAL and ONLINE processing are always visibly distinct - never inferred by the user
 * from behaviour.
 */
enum class ProcessingLocation(val label: String) {
    /** Produced entirely on the device. Nothing left the phone. */
    LOCAL("Local processing"),

    /** The request left the device with the user's explicit opt-in. */
    ONLINE("Online processing"),

    /** No model was involved at all: a tool result, a confirmation, a status line. */
    NONE("On-device action"),
}

/**
 * One turn of conversation.
 *
 * [contextMemoryIds] records which memories were actually used to produce the turn. That is
 * what lets the UI answer "why did you say that?" and what lets deleting a memory show which
 * conversations referenced it.
 */
data class ConversationTurn(
    val id: String,
    val conversationId: String,
    val speaker: Speaker,
    val text: String,
    val createdAt: Long,
    val location: ProcessingLocation = ProcessingLocation.NONE,
    val providerId: String? = null,
    /** Tool calls executed for this turn, for the step-by-step trace view. */
    val toolTrace: List<String> = emptyList(),
    val contextMemoryIds: List<String> = emptyList(),
    /** True when the assistant could not fully satisfy the request and said so. */
    val partial: Boolean = false,
    /** True when this turn came from voice rather than typing. */
    val fromVoice: Boolean = false,
)

/**
 * A conversation. Conversations are stored locally and are individually deletable, which is
 * one of the explicit user controls the requirements list.
 */
data class Conversation(
    val id: String,
    val title: String,
    val createdAt: Long,
    val updatedAt: Long,
    val turns: List<ConversationTurn> = emptyList(),
    /** Set when the user asked for this conversation not to be remembered. */
    val excludedFromMemory: Boolean = false,
) {
    val turnCount: Int get() = turns.size
    val lastTurn: ConversationTurn? get() = turns.lastOrNull()
}

/** Short-lived working context handed to the engine with each request. */
data class DialogueContext(
    val conversationId: String,
    val recentTurns: List<ConversationTurn> = emptyList(),
    val currentApp: String? = null,
    val currentScreenTitle: String? = null,
    /** Id of the plan currently being executed, if any. */
    val activePlanId: String? = null,
    /** True when the device is locked; gates sensitive operations. */
    val deviceLocked: Boolean = false,
    /** True when this request arrived by voice. */
    val fromVoice: Boolean = false,
)
