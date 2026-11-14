package dev.jarvis.core.nlu

import dev.jarvis.core.model.Recurrence

/**
 * Intent vocabulary, entities and parse results.
 *
 * [IntentKind] is deliberately a closed set rather than free-form strings: each kind maps to
 * one or more tools, so the router is a table lookup and a new intent cannot be invented by a
 * mis-parse. Anything that does not match a known kind becomes UNKNOWN or CHITCHAT and is
 * handled by conversation rather than being forced into a tool.
 */
enum class IntentKind(val label: String) {
    // ---- apps and screens -----------------------------------------------------------
    OPEN_APP("Open an app"),
    SEARCH_IN_APP("Search inside an app"),
    READ_SCREEN("Describe the current screen"),
    TAP_ELEMENT("Tap something on screen"),
    SELECT_ORDINAL("Pick the Nth item on screen"),
    TYPE_TEXT("Type text into a field"),
    SCROLL("Scroll"),
    GO_BACK("Go back"),
    GO_HOME("Go home"),

    // ---- media ----------------------------------------------------------------------
    MEDIA_PLAY("Play"),
    MEDIA_PAUSE("Pause"),
    MEDIA_RESUME("Resume"),
    MEDIA_NEXT("Next track or video"),
    MEDIA_PREVIOUS("Previous track"),
    MEDIA_SKIP("Skip forward or back"),
    MEDIA_STOP("Stop"),
    MEDIA_STATUS("What is playing"),
    SET_VOLUME("Set the volume"),

    // ---- messaging and calls --------------------------------------------------------
    SEND_MESSAGE("Send a message"),
    REPLY_TO_LATEST("Reply to the latest message"),
    READ_MESSAGES("Read incoming messages"),
    READ_NOTIFICATIONS("Read notifications"),
    MAKE_CALL("Call someone"),

    // ---- memory ---------------------------------------------------------------------
    REMEMBER("Remember something"),
    DO_NOT_REMEMBER("Do not remember this"),
    FORGET_TOPIC("Forget a topic"),
    FORGET_ALL("Delete all memories"),
    LIST_MEMORIES("What do you remember about me"),
    QUERY_MEMORY("What do you remember about X"),

    // ---- tasks, notes, time ---------------------------------------------------------
    CREATE_REMINDER("Set a reminder"),
    CREATE_ALARM("Set an alarm"),
    START_TIMER("Start a timer"),
    CANCEL_TIMER("Cancel a timer"),
    CREATE_TASK("Add a task"),
    COMPLETE_TASK("Mark a task done"),
    LIST_TASKS("List my tasks"),
    SUMMARIZE_TASKS("Summarise my tasks"),
    CREATE_NOTE("Save a note"),
    LIST_NOTES("List my notes"),

    // ---- automation -----------------------------------------------------------------
    CREATE_AUTOMATION("Create an automation"),
    LIST_AUTOMATIONS("List my automations"),
    TOGGLE_AUTOMATION("Enable or disable an automation"),
    DELETE_AUTOMATION("Delete an automation"),

    // ---- teaching and knowledge -----------------------------------------------------
    TEACH("Teach me a topic"),
    CONTINUE_LESSON("Continue the lesson"),
    ANSWER_QUESTION("Answer to a practice question"),
    EXPLAIN("Explain something"),
    QUERY_KNOWLEDGE("Ask about my documents"),
    INGEST_KNOWLEDGE("Add a document to the knowledge base"),
    LEARNING_PROGRESS("How am I doing"),

    // ---- assistant configuration ----------------------------------------------------
    RENAME_ASSISTANT("Change your name"),
    SET_PREFERENCE("Change a preference"),
    DEVICE_STATUS("Device status"),
    CHANGE_SETTING("Change a device setting"),
    AUTHENTICATE("Secret phrase"),
    ASK_CAPABILITIES("What can you do"),
    PRIVACY_CONTROL("Export or delete my data"),

    // ---- offline computation ----------------------------------------------------------
    // These need no permission, no network and no model, so they are always available.
    CALCULATE("Do arithmetic"),
    CONVERT_UNITS("Convert between units"),

    // ---- online ---------------------------------------------------------------------
    WEB_SEARCH("Search the web"),
    OPEN_URL("Open a link"),
    ASK_ONLINE_AI("Ask the online AI"),

    // ---- dialogue control -----------------------------------------------------------
    CONFIRM("Yes, go ahead"),
    DENY("No"),
    CANCEL("Stop what you are doing"),
    REPEAT("Say that again"),
    GREETING("Hello"),
    THANKS("Thanks"),
    CHITCHAT("Small talk"),
    UNKNOWN("Not understood"),
}

/** Kinds that operate on the assistant's own conversation state rather than the world. */
val DIALOGUE_INTENTS = setOf(
    IntentKind.CONFIRM, IntentKind.DENY, IntentKind.CANCEL, IntentKind.REPEAT,
    IntentKind.GREETING, IntentKind.THANKS, IntentKind.CHITCHAT, IntentKind.UNKNOWN,
)

enum class EntityKind {
    APP_NAME,
    CONTACT,
    MESSAGE_BODY,
    RECIPIENT,
    TOPIC,
    QUERY,
    TIME,
    DURATION,
    ORDINAL,
    NUMBER,
    PERCENT,
    VOLUME_STREAM,
    DIRECTION,
    ELEMENT_DESCRIPTION,
    NOTE_BODY,
    TASK_TITLE,
    ASSISTANT_NAME,
    DOCUMENT,
    SETTING,
    SETTING_VALUE,
    AUTOMATION_TRIGGER,
    AUTOMATION_ACTION,
    URL,
    LEVEL,
    PREFERENCE_KEY,
    PREFERENCE_VALUE,
}

data class Entity(
    val kind: EntityKind,
    /** Normalised value, ready to be handed to a tool. */
    val value: String,
    /** The user's own words, for display and for confirmation prompts. */
    val raw: String = value,
    val confidence: Double = 1.0,
) {
    fun asInt(): Int? = value.toIntOrNull()
    fun asLong(): Long? = value.toLongOrNull()
}

/** A scored candidate intent, so ambiguity can be surfaced instead of guessed at. */
data class IntentCandidate(
    val kind: IntentKind,
    val confidence: Double,
    /** Which rule fired, e.g. "starts with 'remind me'". Used in diagnostics. */
    val reason: String,
)

/**
 * One parsed request.
 *
 * [steps] is non-null when the user asked for several things in one sentence - the
 * multi-step case from requirement §4. Each step is itself a [ParsedRequest] with one intent,
 * which is what lets the planner turn a sentence into a [dev.jarvis.core.agent.Plan] without a
 * second grammar.
 */
data class ParsedRequest(
    val originalText: String,
    val intent: IntentKind,
    val confidence: Double,
    val entities: List<Entity> = emptyList(),
    val alternatives: List<IntentCandidate> = emptyList(),
    val steps: List<ParsedRequest>? = null,
    /** Anything the parser noticed and wants to tell the user, e.g. an ambiguous ordinal. */
    val notes: List<String> = emptyList(),
    /** Where the request came from, which changes what is safe to do. */
    val fromVoice: Boolean = false,
    /**
     * When the request names a point in time, as understood - not yet resolved to an instant.
     *
     * Resolution needs "now", which belongs to the caller. An [TimeReference.Unresolved] here means
     * the user said something time-like that we could not parse; that must be asked about, never
     * silently defaulted to "now".
     */
    val time: TimeReference? = null,
    /** A span rather than a point, e.g. "for 10 minutes" on a timer. */
    val durationMillis: Long? = null,
    /** Set when [time] is a [TimeReference.RecurringAt]; kept here for convenient access. */
    val recurrence: Recurrence? = time?.let { (it as? TimeReference.RecurringAt)?.recurrence },
    /**
     * True when this request must not be persisted: secret phrases, and explicit "don't remember
     * this" opt-outs.
     *
     * Storage and logging layers check this flag before writing a conversation turn. It is not a
     * guarantee on its own - it is the signal that makes the guarantee enforceable in one place.
     */
    val sensitive: Boolean = false,
) {
    val isMultiStep: Boolean get() = steps != null && steps.size > 1

    fun entity(kind: EntityKind): Entity? = entities.firstOrNull { it.kind == kind }
    fun entityValue(kind: EntityKind): String? = entity(kind)?.value
    fun ordinal(): Int? = entity(EntityKind.ORDINAL)?.asInt()
    fun number(): Int? = entity(EntityKind.NUMBER)?.asInt()

    /** All leaf requests, in order. For a single request that is just this one. */
    fun flatten(): List<ParsedRequest> = steps?.flatMap { it.flatten() } ?: listOf(this)

    /** Confidence of the weakest step, because a chain is only as reliable as its worst link. */
    val weakestConfidence: Double
        get() = flatten().minOfOrNull { it.confidence } ?: confidence
}
