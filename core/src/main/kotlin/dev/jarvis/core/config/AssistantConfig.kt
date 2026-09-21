package dev.jarvis.core.config

import dev.jarvis.core.model.DifficultyLevel
import dev.jarvis.core.model.LearningStyle
import dev.jarvis.core.util.JsonNumber
import dev.jarvis.core.util.JsonObject
import dev.jarvis.core.util.json
import dev.jarvis.core.util.parseJsonOrNull

/** How the assistant talks and behaves. User-configurable. */
enum class ResponseStyle(val label: String) {
    CONCISE("Short and direct"),
    DETAILED("Thorough"),
    CASUAL("Casual and friendly"),
    TECHNICAL("Technical"),
    STEP_BY_STEP("Always numbered steps"),
}

sealed class NicknameValidation {
    /**
     * The name is usable. [warning] is non-null when the name is accepted but has a
     * consequence the user should be told about - it is never a silent downgrade.
     */
    data class Accepted(val name: String, val warning: String? = null) : NicknameValidation()

    data class Rejected(val reason: String) : NicknameValidation()
}

/**
 * Assistant configuration.
 *
 * Everything here is stored locally and is user-editable. Two invariants matter:
 *
 *  1. [nickname] is the assistant's name. It is entirely the user's choice, can be anything,
 *     and can be changed at any time without losing data - no other state keys off it.
 *  2. [onlineAiEnabled] defaults to false. The assistant must be fully useful with no
 *     network, and turning the network on is always an explicit, reversible decision.
 */
data class AssistantConfig(
    // ---- identity -----------------------------------------------------------------
    val nickname: String = Companion.DEFAULT_NICKNAME,
    val userName: String? = null,

    // ---- behaviour ----------------------------------------------------------------
    val responseStyle: ResponseStyle = ResponseStyle.CONCISE,
    val speakResponses: Boolean = false,
    val learningStyle: LearningStyle = LearningStyle.MIXED,
    val defaultTeachingLevel: DifficultyLevel = DifficultyLevel.BEGINNER,
    val locale: String = "en",

    // ---- privacy ------------------------------------------------------------------
    val memoryEnabled: Boolean = true,
    val onlineAiEnabled: Boolean = false,
    val onlineSearchEnabled: Boolean = false,
    /** Keeping transcripts of what the user said is opt-in, not opt-out. */
    val voiceHistoryEnabled: Boolean = false,
    val inferPreferences: Boolean = true,
    /** Consequential actions always ask first unless the user explicitly relaxes this. */
    val confirmConsequentialActions: Boolean = true,

    // ---- voice --------------------------------------------------------------------
    val wakeWordEnabled: Boolean = false,
    val wakeWord: String = Companion.DEFAULT_NICKNAME,
    val ttsVoiceId: String? = null,
    val ttsPitch: Float = 1.0f,
    val ttsRate: Float = 1.0f,

    // ---- scheduling ---------------------------------------------------------------
    /**
     * Named times of day in 24h hours, used to resolve "after dinner" or "in the morning".
     * Editable, because dinner is at 8pm for some people and 10pm for others.
     */
    val daypartHours: Map<String, Int> = Companion.DEFAULT_DAYPARTS,

    // ---- providers ----------------------------------------------------------------
    val onlineProviderId: String? = null,
    /** Identifier of an installed on-device model, when the user has added one. */
    val localModelId: String? = null,

    // ---- screen agent -------------------------------------------------------------
    val screenAgentEnabled: Boolean = true,
    /** Cap on nodes kept in a snapshot, so a heavy screen cannot exhaust memory. */
    val maxSnapshotNodes: Int = 1200,
    /** Maximum steps the agent takes for one request before stopping and reporting. */
    val maxAgentSteps: Int = 12,
) {

    /**
     * Validates a proposed nickname.
     *
     * The name is free-form on purpose: any language, any word, emoji allowed. The only
     * rejections are the ones that would actually break something - blank, too long to be
     * spoken or rendered, containing line breaks, or made only of characters that vanish
     * during normalisation (which would make wake-word matching silently impossible).
     */
    fun validateNickname(candidate: String): NicknameValidation {
        val trimmed = candidate.trim()
        if (trimmed.isEmpty()) return NicknameValidation.Rejected("That name is empty.")
        if (trimmed.length > MAX_NICKNAME_LENGTH) {
            return NicknameValidation.Rejected(
                "That name is ${trimmed.length} characters; the limit is $MAX_NICKNAME_LENGTH " +
                    "so that it can still be spoken and displayed.",
            )
        }
        // A name with no letters or digits is still the user's choice - "🤖" is a perfectly
        // good name to display. It is accepted, with an honest warning that a name with no
        // pronounceable content cannot work as a spoken wake word. Refusing it would be the
        // assistant overriding a harmless personal preference.
        if (trimmed.none { it.isLetterOrDigit() }) {
            return NicknameValidation.Accepted(
                trimmed,
                warning = "That name has no letters or numbers, so it will show up fine but " +
                    "cannot be used as a spoken wake word.",
            )
        }
        if ('\n' in trimmed || '\t' in trimmed) {
            return NicknameValidation.Rejected("A name cannot contain line breaks or tabs.")
        }
        if (trimmed.any { it.isISOControl() }) {
            return NicknameValidation.Rejected("A name cannot contain control characters.")
        }
        return NicknameValidation.Accepted(trimmed)
    }

    /** Returns the renamed config, or null with the reason available via [validateNickname]. */
    fun renamedTo(candidate: String): AssistantConfig? = when (val result = validateNickname(candidate)) {
        is NicknameValidation.Accepted -> copy(nickname = result.name, wakeWord = result.name)
        is NicknameValidation.Rejected -> null
    }

    fun daypartHour(name: String): Int? = daypartHours[name.trim().lowercase()]

    /** Serialises to the dependency-free JSON model in :core. */
    fun toJson(): JsonObject = JsonObject(
        linkedMapOf(
            "nickname" to json(nickname),
            "userName" to json(userName),
            "responseStyle" to json(responseStyle.name),
            "speakResponses" to json(speakResponses),
            "learningStyle" to json(learningStyle.name),
            "defaultTeachingLevel" to json(defaultTeachingLevel.name),
            "locale" to json(locale),
            "memoryEnabled" to json(memoryEnabled),
            "onlineAiEnabled" to json(onlineAiEnabled),
            "onlineSearchEnabled" to json(onlineSearchEnabled),
            "voiceHistoryEnabled" to json(voiceHistoryEnabled),
            "inferPreferences" to json(inferPreferences),
            "confirmConsequentialActions" to json(confirmConsequentialActions),
            "wakeWordEnabled" to json(wakeWordEnabled),
            "wakeWord" to json(wakeWord),
            "ttsVoiceId" to json(ttsVoiceId),
            "ttsPitch" to json(ttsPitch.toDouble()),
            "ttsRate" to json(ttsRate.toDouble()),
            "daypartHours" to JsonObject(daypartHours.mapValues { json(it.value) }),
            "onlineProviderId" to json(onlineProviderId),
            "localModelId" to json(localModelId),
            "screenAgentEnabled" to json(screenAgentEnabled),
            "maxSnapshotNodes" to json(maxSnapshotNodes),
            "maxAgentSteps" to json(maxAgentSteps),
        ),
    )

    companion object {
        const val DEFAULT_NICKNAME = "Jarvis"
        const val MIN_NICKNAME_LENGTH = 1
        const val MAX_NICKNAME_LENGTH = 32

        val DEFAULT_DAYPARTS: Map<String, Int> = linkedMapOf(
            "early morning" to 5,
            "morning" to 8,
            "breakfast" to 8,
            "noon" to 12,
            "lunch" to 13,
            "afternoon" to 15,
            "evening" to 18,
            "dinner" to 20,
            "night" to 22,
            "bedtime" to 23,
        )

        val DEFAULT: AssistantConfig = AssistantConfig()

        /**
         * Deserialises, tolerating unknown and missing keys.
         *
         * Tolerance is a correctness requirement rather than a nicety: a config written by a
         * future version must still load, and a corrupt value must fall back to its default
         * instead of locking the user out of their own assistant.
         */
        fun fromJson(text: String?): AssistantConfig {
            val root = parseJsonOrNull(text) as? JsonObject ?: return DEFAULT

            fun str(key: String): String? = root.string(key)
            fun bool(key: String, default: Boolean): Boolean = root.bool(key) ?: default
            fun int(key: String, default: Int): Int = root.int(key) ?: default
            fun float(key: String, default: Float): Float = root.double(key)?.toFloat() ?: default
            fun <E : Enum<E>> enumOf(key: String, values: Array<E>, default: E): E {
                val name = str(key) ?: return default
                return values.firstOrNull { it.name.equals(name, ignoreCase = true) } ?: default
            }

            val dayparts: Map<String, Int>? = root.obj("daypartHours")
                ?.entries
                ?.mapNotNull { (key, value) ->
                    val number = (value as? JsonNumber)?.value?.toInt()
                    if (number != null) key to number else null
                }
                ?.toMap()
                ?.takeIf { it.isNotEmpty() }

            return DEFAULT.copy(
                nickname = str("nickname")?.takeIf { it.isNotBlank() } ?: DEFAULT.nickname,
                userName = str("userName"),
                responseStyle = enumOf(
                    "responseStyle",
                    ResponseStyle.entries.toTypedArray(),
                    DEFAULT.responseStyle,
                ),
                speakResponses = bool("speakResponses", DEFAULT.speakResponses),
                learningStyle = enumOf(
                    "learningStyle",
                    LearningStyle.entries.toTypedArray(),
                    DEFAULT.learningStyle,
                ),
                defaultTeachingLevel = enumOf(
                    "defaultTeachingLevel",
                    DifficultyLevel.entries.toTypedArray(),
                    DEFAULT.defaultTeachingLevel,
                ),
                locale = str("locale") ?: DEFAULT.locale,
                memoryEnabled = bool("memoryEnabled", DEFAULT.memoryEnabled),
                onlineAiEnabled = bool("onlineAiEnabled", DEFAULT.onlineAiEnabled),
                onlineSearchEnabled = bool("onlineSearchEnabled", DEFAULT.onlineSearchEnabled),
                voiceHistoryEnabled = bool("voiceHistoryEnabled", DEFAULT.voiceHistoryEnabled),
                inferPreferences = bool("inferPreferences", DEFAULT.inferPreferences),
                confirmConsequentialActions = bool(
                    "confirmConsequentialActions",
                    DEFAULT.confirmConsequentialActions,
                ),
                wakeWordEnabled = bool("wakeWordEnabled", DEFAULT.wakeWordEnabled),
                wakeWord = str("wakeWord")?.takeIf { it.isNotBlank() } ?: DEFAULT.wakeWord,
                ttsVoiceId = str("ttsVoiceId"),
                ttsPitch = float("ttsPitch", DEFAULT.ttsPitch),
                ttsRate = float("ttsRate", DEFAULT.ttsRate),
                daypartHours = dayparts ?: DEFAULT.daypartHours,
                onlineProviderId = str("onlineProviderId"),
                localModelId = str("localModelId"),
                screenAgentEnabled = bool("screenAgentEnabled", DEFAULT.screenAgentEnabled),
                maxSnapshotNodes = int("maxSnapshotNodes", DEFAULT.maxSnapshotNodes).coerceIn(50, 20_000),
                maxAgentSteps = int("maxAgentSteps", DEFAULT.maxAgentSteps).coerceIn(1, 50),
            )
        }
    }
}
