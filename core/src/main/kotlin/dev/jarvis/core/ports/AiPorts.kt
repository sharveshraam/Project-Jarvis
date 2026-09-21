package dev.jarvis.core.ports

import dev.jarvis.core.capability.DeviceCapabilityProbe
import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.model.ConversationTurn
import dev.jarvis.core.model.ProcessingLocation

/**
 * AI provider ports.
 *
 * The architecture the requirements ask for is LOCAL AI + OPTIONAL EXTERNAL AI, with both
 * replaceable. So the core defines what a provider *is* and what it may be sent, and never
 * names a vendor. Adding a provider means implementing one interface in `:app`.
 */

/** What kind of generation the engine needs. Providers declare which of these they can do. */
enum class AiTask {
    /** Open-ended reply to the user. */
    CONVERSE,

    /** Turn a request into ordered steps. */
    PLAN,

    /** Produce an explanation of a concept at a given level. */
    TEACH_EXPLAIN,

    /** Produce a practice problem with a known answer. */
    TEACH_QUESTION,

    /** Mark a learner's attempt and explain the mistake. */
    EVALUATE_ANSWER,

    /** Compress text: a day's tasks, a document, a notification thread. */
    SUMMARIZE,

    /** Pull structured fields out of free text. */
    EXTRACT,

    /** Order candidates by relevance to a query. */
    RANK,

    /** Rephrase something the assistant already produced, e.g. for a different style. */
    PARAPHRASE,
}

/**
 * One generation request.
 *
 * The privacy-critical fields are [mayIncludeMemory] and [mayIncludeHistory]. The router sets
 * them, and a provider implementation must respect them: turning online AI on does not mean
 * the user's memory and conversation history are now available to a remote service. They are
 * sent only when the specific request needs them, and only when the user has opted in.
 */
data class AiRequest(
    val requestId: String,
    val task: AiTask,
    val prompt: String,
    val systemPrompt: String? = null,
    val context: List<ConversationTurn> = emptyList(),
    val maxTokens: Int? = null,
    val temperature: Double? = null,
    val mayIncludeMemory: Boolean = false,
    val mayIncludeHistory: Boolean = false,
    /** Retrieved local material the provider may ground on, with attribution. */
    val grounding: List<String> = emptyList(),
)

data class AiResponse(
    val requestId: String,
    val text: String,
    val location: ProcessingLocation,
    val providerId: String,
    val model: String?,
    /** True when served from a local cache of a previously approved online fetch. */
    val cached: Boolean = false,
    val latencyMillis: Long = 0L,
    /**
     * Shown to the user verbatim when present. This is how a provider discloses that it used
     * the network, that it truncated, or that it refused.
     */
    val notice: String? = null,
)

sealed class AiOutcome {
    data class Ok(val response: AiResponse) : AiOutcome()

    /** Not usable right now; the router may fall back to another provider. */
    data class Unavailable(val reason: String, val capabilityId: String? = null) : AiOutcome()

    /** Tried and failed. [retryable] decides whether the router tries again. */
    data class Failed(val reason: String, val retryable: Boolean = true) : AiOutcome()
}

/**
 * Anything that can generate text for the assistant.
 *
 * Implementations: the deterministic on-device engine (always present), an optional on-device
 * neural model, and optional online providers.
 */
interface AiProvider {
    val id: String
    val displayName: String
    val location: ProcessingLocation
    val tasks: Set<AiTask>

    /**
     * Whether this provider can be used right now, given the device and the user's config.
     *
     * Consulted before every request. An online provider must return false when online features
     * are disabled, regardless of connectivity.
     */
    fun isAvailable(probe: DeviceCapabilityProbe, config: AssistantConfig): Boolean

    fun canHandle(task: AiTask): Boolean = task in tasks

    fun complete(request: AiRequest): AiOutcome
}

/**
 * The slot for an on-device neural model (MediaPipe LLM Inference, llama.cpp, ONNX, or
 * whatever the user installs).
 *
 * No model is bundled: a useful one is hundreds of megabytes and its viability is
 * device-dependent. The core treats "no model installed" as a normal, reported state
 * ([CapabilityStatus.MODEL_REQUIRED]) and routes around it - it does not pretend a
 * deterministic engine is a language model.
 */
interface LocalModelPort : AiProvider {
    /** Ids of models available to install, for the Settings screen. */
    fun availableModels(): List<LocalModelInfo>

    fun installedModel(): LocalModelInfo?

    fun install(modelId: String, onProgress: (Float) -> Unit): PortResult

    fun uninstall(modelId: String): PortResult

    /** Peak memory the model needs, so the UI can warn before installing on a small device. */
    fun estimatedRamBytes(modelId: String): Long?
}

data class LocalModelInfo(
    val id: String,
    val displayName: String,
    val sizeBytes: Long,
    val languages: List<String>,
    val source: String,
    val installed: Boolean,
)

/** An online provider, used only with explicit opt-in and always labelled as online. */
interface OnlineAiPort : AiProvider {
    /** True when an API key is configured. Absent a key this is Unavailable, not Failed. */
    fun isConfigured(): Boolean

    /** The exact endpoint a request would go to, shown to the user before they enable it. */
    fun endpointDescription(): String

    /**
     * What would be sent, in plain language, for a given request.
     *
     * Shown in the confirmation UI so "this request leaves your device" is specific rather than
     * a vague warning.
     */
    fun describeOutgoingPayload(request: AiRequest): String
}

data class WebSearchResult(
    val title: String,
    val url: String,
    val snippet: String,
)

sealed class WebSearchOutcome {
    data class Ok(val results: List<WebSearchResult>, val providerId: String) : WebSearchOutcome()
    data class Unavailable(val reason: String) : WebSearchOutcome()
    data class Failed(val reason: String) : WebSearchOutcome()
}

/** Optional web search. Online-only by definition, and off unless the user enables it. */
interface WebSearchPort {
    fun search(query: String, maxResults: Int = 5): WebSearchOutcome
    fun fetchPageText(url: String): PortResult
}
