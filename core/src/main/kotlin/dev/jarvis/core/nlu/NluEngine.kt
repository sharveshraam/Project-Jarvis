package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.util.Text

/**
 * Turns a sentence into a [ParsedRequest]: one intent, its arguments, its time, and - when the user
 * asked for several things - the individual steps.
 *
 * ## The pipeline
 *
 * 1. Strip a leading wake word or nickname ([IntentRules.stripAddress]).
 * 2. Look for a step separator and, if splitting yields two independently confident requests,
 *    parse each half on its own ([ParsedRequest.steps]).
 * 3. Score every rule ([IntentRules.match]) and pick the best, adjusted for what the conversation
 *    is currently waiting for ([NluContext]).
 * 4. Extract slot values ([EntityExtractor]) and time ([TemporalParser]).
 * 5. Record what we are unsure about in [ParsedRequest.notes] and [ParsedRequest.alternatives].
 *
 * ## The honesty rule
 *
 * Nothing here is allowed to guess. An unparsable time becomes [TimeReference.Unresolved] and a
 * note, not "now". A missing app name stays missing, so the caller asks. Two close candidates are
 * both reported rather than one being silently dropped. The engine would rather produce a request
 * the agent has to clarify than a confident wrong action.
 *
 * ## Replaceability
 *
 * Rules are the floor, not the ceiling. [IntentReranker] lets an on-device model reorder the
 * candidates, but it can only reorder what the rules already recognised - it cannot introduce an
 * intent that no rule matched, so a hallucinated classification can never reach a tool.
 */
class NluEngine(
    /** Supplies the live config, so a renamed assistant is picked up without restarting the engine. */
    private val configProvider: () -> AssistantConfig,
    private val temporal: TemporalParser,
    private val reranker: IntentReranker? = null,
) {

    /**
     * Optional re-ranking by a model (on-device neural slot or an online provider).
     *
     * Returns the candidates in the order the model prefers. Implementations must not invent kinds:
     * [NluEngine] discards anything that was not in the rule-produced list.
     */
    fun interface IntentReranker {
        fun rerank(text: String, candidates: List<IntentCandidate>): List<IntentCandidate>
    }

    /** What the conversation is waiting for, which changes how short utterances are read. */
    data class NluContext(
        /** A confirmation was just asked for, so "yes" means yes rather than being noise. */
        val awaitingConfirmation: Boolean = false,
        /** A lesson is running, so an otherwise unrecognisable reply is an answer. */
        val activeLesson: Boolean = false,
        /** The previous request, used to resolve "again" and bare follow-ups. */
        val lastIntent: IntentKind? = null,
    ) {
        companion object {
            val IDLE = NluContext()
        }
    }

    companion object {
        /** Below this, the best guess is reported but flagged as needing confirmation. */
        const val LOW_CONFIDENCE = 0.60

        /** Two candidates within this margin are ambiguous; see [IntentRules.AMBIGUITY_MARGIN]. */
        val AMBIGUITY_MARGIN = IntentRules.AMBIGUITY_MARGIN

        /** Sentence separators that may introduce a second step, strongest first. */
        private val STRONG_SEPARATORS = listOf(" and then ", " after that ", "; ", ", then ")
        private val WEAK_SEPARATORS = listOf(" then ", ", and ", " and ", " also ", ", ")

        /** Guard against a pathological chain of splits; 4 steps covers real usage. */
        private const val MAX_STEPS = 4
        private const val MAX_SPLIT_DEPTH = 2

        /** Intents whose meaning changes if a time is present. */
        private val TIME_BEARING = setOf(
            IntentKind.CREATE_REMINDER, IntentKind.CREATE_ALARM, IntentKind.START_TIMER,
            IntentKind.CANCEL_TIMER, IntentKind.CREATE_TASK, IntentKind.CREATE_NOTE,
            IntentKind.CREATE_AUTOMATION, IntentKind.CHANGE_SETTING, IntentKind.SET_PREFERENCE,
            IntentKind.MEDIA_SKIP, IntentKind.QUERY_MEMORY, IntentKind.OPEN_APP,
        )

        /**
         * Intents whose text must never be written to conversation memory, the activity log, or a
         * crash report.
         *
         * AUTHENTICATE carries the secret phrase in the user's own words; persisting it would put a
         * credential somewhere the user does not expect and cannot see. DO_NOT_REMEMBER is an
         * explicit opt-out, so honouring it means not recording the opt-out itself as content.
         */
        private val SENSITIVE_INTENTS = setOf(
            IntentKind.AUTHENTICATE, IntentKind.DO_NOT_REMEMBER,
        )

        /** Intents that are about the conversation rather than the world. */
        private val DIALOGUE_ONLY = setOf(
            IntentKind.CONFIRM, IntentKind.DENY, IntentKind.CANCEL, IntentKind.REPEAT,
            IntentKind.GREETING, IntentKind.THANKS,
        )

        private val TIME_ANCHORS = Regex(
            "\\b(at|in|on|by|for|after|before|every|each|tomorrow|today|tonight|yesterday|next|this" +
                "|monday|tuesday|wednesday|thursday|friday|saturday|sunday|weekend|weekday" +
                "|morning|afternoon|evening|night|noon|midnight|am|pm|daily|weekly|monthly" +
                // Vague markers: they produce TimeReference.Unresolved, which is what turns
                // "remind me later" into "when?" instead of a reminder that fires immediately.
                "|later|soon|sometime|some time|whenever)\\b",
        )

        /** Where a time phrase is unlikely to continue past. */
        private val TIME_CUTS = listOf(" to ", " and ", " then ", " but ", ",", ";")
    }

    /**
     * Parse one user request.
     *
     * Never throws and never returns null: an unparseable sentence comes back as
     * [IntentKind.UNKNOWN] with a note explaining that, which is what lets the agent say
     * "I didn't catch that" instead of doing something arbitrary.
     */
    fun parse(
        rawText: String,
        fromVoice: Boolean = false,
        context: NluContext = NluContext.IDLE,
    ): ParsedRequest {
        val config = configProvider()
        val trimmed = rawText.trim()
        if (trimmed.isEmpty()) {
            return ParsedRequest(
                originalText = rawText,
                intent = IntentKind.UNKNOWN,
                confidence = 0.0,
                notes = listOf("empty request"),
                fromVoice = fromVoice,
            )
        }

        val addressed = IntentRules.isAddressedTo(trimmed, config)
        val body = IntentRules.stripAddress(trimmed, config)

        // "hey jarvis" on its own is a greeting, not a request with an empty body.
        if (body.isBlank()) {
            return ParsedRequest(
                originalText = rawText,
                intent = IntentKind.GREETING,
                confidence = 0.90,
                alternatives = listOf(IntentCandidate(IntentKind.GREETING, 0.90, "wake word alone")),
                notes = if (addressed) listOf("addressed by name with no request") else emptyList(),
                fromVoice = fromVoice,
            )
        }

        return parseBody(body, originalText = rawText, fromVoice, context, config, depth = 0)
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private fun parseBody(
        body: String,
        originalText: String,
        fromVoice: Boolean,
        context: NluContext,
        config: AssistantConfig,
        depth: Int,
    ): ParsedRequest {
        val steps = if (depth < MAX_SPLIT_DEPTH) splitIntoSteps(body, config) else null
        if (steps != null && steps.size > 1) {
            val parsedSteps = steps.map { part ->
                parseBody(part, part, fromVoice, context, config, depth + 1)
            }
            val first = parsedSteps.first()
            return ParsedRequest(
                originalText = originalText,
                // A multi-step request is routed through its steps; the container carries the first
                // step's intent so that a caller which ignores `steps` still does something sane
                // rather than treating the whole sentence as UNKNOWN.
                intent = first.intent,
                confidence = parsedSteps.minOf { it.confidence },
                entities = first.entities,
                alternatives = first.alternatives,
                steps = parsedSteps,
                time = first.time,
                durationMillis = first.durationMillis,
                notes = buildList {
                    add("split into ${parsedSteps.size} steps")
                    parsedSteps.forEachIndexed { index, step ->
                        step.notes.forEach { note -> add("step ${index + 1}: $note") }
                    }
                },
                fromVoice = fromVoice,
            )
        }

        val hits = IntentRules.match(body, config)
        if (hits.isEmpty()) {
            return unknown(body, originalText, fromVoice, context, "no rule matched")
        }

        val ranked = applyReranker(body, hits)
        var best = ranked.first()
        val alternatives = ranked.drop(1)
        val notes = ArrayList<String>(3)

        // --- Context adjustment -----------------------------------------------------
        // A bare "yes" only means CONFIRM when a confirmation was actually asked for. Otherwise
        // "yes, open settings" is a request that happens to start politely.
        if (best.kind in DIALOGUE_ONLY && !context.awaitingConfirmation &&
            best.kind != IntentKind.GREETING && best.kind != IntentKind.THANKS
        ) {
            val content = ranked.firstOrNull { it.kind !in DIALOGUE_ONLY && it.kind != IntentKind.CHITCHAT }
            val shortUtterance = Text.tokenize(body).size <= 2
            if (content != null && !shortUtterance && best.confidence - content.confidence <= 0.20) {
                notes += "ignored '${best.kind.name.lowercase()}' lead-in; no confirmation was pending"
                best = content
            }
        }

        // During a lesson, an otherwise unrecognisable reply is the learner's answer.
        if (context.activeLesson && best.confidence < LOW_CONFIDENCE &&
            best.kind != IntentKind.ANSWER_QUESTION
        ) {
            val answer = ranked.firstOrNull { it.kind == IntentKind.ANSWER_QUESTION }
            best = answer ?: RuleHit(
                kind = IntentKind.ANSWER_QUESTION,
                confidence = maxOf(best.confidence, 0.62),
                reason = "treated as a lesson answer while teaching mode is active",
                priority = 8,
                matchedEntities = listOf(Entity(EntityKind.QUERY, Text.normalize(body), body)),
            )
            notes += "teaching mode is active, so this was read as an answer"
        }

        // --- Ambiguity ---------------------------------------------------------------
        val runnerUp = alternatives.firstOrNull { it.kind != best.kind }
        val ambiguous = runnerUp != null && best.confidence - runnerUp.confidence <= AMBIGUITY_MARGIN
        if (ambiguous && runnerUp != null) {
            notes += "ambiguous between ${best.kind.name} and ${runnerUp.kind.name}"
        }
        if (best.confidence < LOW_CONFIDENCE) {
            notes += "low confidence (${twoDecimals(best.confidence)})"
        }

        // --- Slots and time -----------------------------------------------------------
        val entities = ArrayList<Entity>(best.matchedEntities)
        fun addUnique(entity: Entity?) {
            if (entity != null && entity.value.isNotBlank() && entities.none { it.kind == entity.kind }) {
                entities += entity
            }
        }

        var time: TimeReference? = null
        var durationMillis: Long? = null
        if (best.kind in TIME_BEARING) {
            val found = extractTemporal(body, best.kind)
            time = found.first
            durationMillis = found.second
            if (time is TimeReference.Unresolved) {
                notes += "could not understand the time '${time.phrase}'"
            }
            if (durationMillis != null) {
                addUnique(Entity(EntityKind.DURATION, durationMillis.toString(), time?.phrase ?: body))
            }
            val resolved = time?.let { if (it !is TimeReference.Unresolved) temporal.resolve(it) else null }
            if (resolved != null) {
                addUnique(Entity(EntityKind.TIME, resolved.toString(), time?.phrase ?: body))
            }
        }

        // "remind me to call mom in 5 minutes" should store the reminder as "call mom". The time
        // belongs to `time`; leaving it inside the text would make the reminder read as a sentence
        // about a time instead of the thing to do.
        val cleanedEntities = stripTimePhrase(entities, time)

        // A required slot that is missing is the caller's cue to ask, not to assume.
        notes += missingSlotNote(best.kind, cleanedEntities)

        return ParsedRequest(
            originalText = originalText,
            intent = best.kind,
            confidence = best.confidence,
            entities = cleanedEntities,
            alternatives = ranked.map { it.toCandidate() }.filter { it.kind != best.kind },
            notes = notes,
            fromVoice = fromVoice,
            time = time,
            durationMillis = durationMillis,
            sensitive = best.kind in SENSITIVE_INTENTS,
        )
    }

    /** Content slots that should carry the task text without the time phrase attached. */
    private val CONTENT_SLOTS = setOf(EntityKind.QUERY, EntityKind.NOTE_BODY, EntityKind.TOPIC)

    private fun stripTimePhrase(entities: List<Entity>, time: TimeReference?): List<Entity> {
        if (time == null || time is TimeReference.Unresolved) return entities
        val phrase = Text.normalize(time.phrase).trim()
        if (phrase.isBlank()) return entities
        return entities.mapNotNull { entity ->
            if (entity.kind !in CONTENT_SLOTS) return@mapNotNull entity
            val normalizedValue = Text.normalize(entity.value).trim()
            // The slot held nothing but the time, so there is no content to keep: "wake me at 7am"
            // has an alarm time and no message. Dropping it is what makes the caller ask.
            if (normalizedValue.isNotEmpty() && phrase.contains(normalizedValue)) return@mapNotNull null
            val trimmed = removeSpan(normalizedValue, phrase)
            if (trimmed.isBlank()) return@mapNotNull null
            if (trimmed == entity.value) entity else entity.copy(value = trimmed, raw = trimmed)
        }
    }

    private fun removeSpan(value: String, span: String): String {
        val index = value.indexOf(span)
        if (index < 0) return value
        return (value.substring(0, index) + " " + value.substring(index + span.length))
            .trim()
            .trim(',', '.', ':', ';')
            .trim()
    }

    /** Locale-independent two-decimal formatting, for notes that end up in logs. */
    private fun twoDecimals(value: Double): String {
        val scaled = kotlin.math.round(value * 100).toLong()
        return "${scaled / 100}.${(scaled % 100).toString().padStart(2, '0')}"
    }

    private fun unknown(
        body: String,
        originalText: String,
        fromVoice: Boolean,
        context: NluContext,
        reason: String,
    ): ParsedRequest {
        // In teaching mode an unmatched reply is still an answer to the current question.
        if (context.activeLesson) {
            return ParsedRequest(
                originalText = originalText,
                intent = IntentKind.ANSWER_QUESTION,
                confidence = 0.62,
                entities = listOf(Entity(EntityKind.QUERY, Text.normalize(body), body)),
                notes = listOf("teaching mode is active, so this was read as an answer"),
                fromVoice = fromVoice,
            )
        }
        return ParsedRequest(
            originalText = originalText,
            intent = IntentKind.UNKNOWN,
            confidence = 0.0,
            notes = listOf(reason),
            fromVoice = fromVoice,
        )
    }

    /**
     * Let a model reorder the candidates, but only among kinds the rules already produced.
     *
     * This is the replaceable-AI seam for understanding: swapping in an on-device classifier or an
     * online provider changes the ordering, never the vocabulary, and can never make the engine act
     * on an intent that no offline rule recognised.
     */
    private fun applyReranker(body: String, hits: List<RuleHit>): List<RuleHit> {
        val r = reranker ?: return hits
        val allowed = hits.associateBy { it.kind }
        return try {
            val reordered = r.rerank(body, hits.map { it.toCandidate() })
            val known = reordered.mapNotNull { candidate -> allowed[candidate.kind] }
                // Re-scoring is allowed; inventing a kind or duplicating one is not.
                .distinctBy { it.kind }
            val rest = hits.filter { hit -> known.none { it.kind == hit.kind } }
            val merged = known + rest
            if (merged.size == hits.size) merged else hits
        } catch (t: Throwable) {
            // A failing model must degrade to the deterministic result, never to no result.
            hits
        }
    }

    /**
     * Split a compound request into parts, but only when the split is justified.
     *
     * Both halves must independently produce a confident intent. That check is what keeps
     * "play songs by X and Y" from becoming two requests: "Y" alone matches nothing.
     */
    private fun splitIntoSteps(body: String, config: AssistantConfig): List<String>? {
        val normalized = Text.normalize(body)
        if (normalized.length < 12) return null

        for (separator in STRONG_SEPARATORS + WEAK_SEPARATORS) {
            val index = normalized.indexOf(separator)
            if (index <= 0) continue

            val left = normalized.substring(0, index).trim()
            val right = normalized.substring(index + separator.length).trim()
            if (left.length < 3 || right.length < 3) continue
            if (right.split(" ").size > MAX_STEPS * 8) continue

            if (!isIndependentRequest(left, config) || !isIndependentRequest(right, config)) continue

            // Recurse on the right half so "A and B and C" becomes three steps, not two.
            val tail = splitIntoSteps(right, config)?.take(MAX_STEPS - 1) ?: listOf(right)
            return (listOf(left) + tail).take(MAX_STEPS)
        }
        return null
    }

    /** True when a fragment stands on its own as a request. */
    private fun isIndependentRequest(fragment: String, config: AssistantConfig): Boolean {
        val hits = IntentRules.match(fragment, config)
        val best = hits.firstOrNull() ?: return false
        if (best.kind == IntentKind.CHITCHAT || best.kind == IntentKind.UNKNOWN) return false
        // A fragment that is only a time or a name is not a request.
        return best.confidence >= LOW_CONFIDENCE && Text.tokenize(fragment).size >= 2
    }

    /**
     * Find the time or duration in a request.
     *
     * Candidate spans are taken from time-marker anchors, then trimmed at the first clause
     * boundary, so "remind me at 7pm to call mom" yields "at 7pm" rather than trying to parse the
     * whole sentence as a time. Shorter, more precise spans are attempted first.
     *
     * @return the reference and, when the user gave a span rather than a point, the duration.
     */
    private fun extractTemporal(body: String, kind: IntentKind): Pair<TimeReference?, Long?> {
        val normalized = Text.normalize(body)

        // Timers are pure spans: "10 minutes" with no clock time attached.
        if (kind == IntentKind.START_TIMER) {
            val span = temporal.parseDuration(normalized)
            if (span != null) return null to span
        }

        for (anchor in TIME_ANCHORS.findAll(normalized)) {
            val candidate = normalized.substring(anchor.range.first).trim()
            if (candidate.length < 2 || candidate.length > 60) continue

            for (variant in timeVariants(candidate)) {
                val duration = if (kind == IntentKind.START_TIMER || kind == IntentKind.MEDIA_SKIP) {
                    temporal.parseDuration(variant)
                } else {
                    null
                }
                val reference = temporal.parse(variant)
                if (reference == null && duration == null) continue

                // A duration with no clock time is a span; keep it out of `time` for timers.
                if (kind == IntentKind.START_TIMER) {
                    val span = duration ?: (reference as? TimeReference.In)?.offsetMillis
                    if (span != null) return null to span
                }
                if (reference != null) return reference to duration
                if (duration != null) return TimeReference.In(variant, duration) to duration
            }
        }

        // Last resort: a bare span anywhere in the sentence, e.g. "timer 90 seconds".
        val bareDuration = temporal.parseDuration(normalized)
        if (bareDuration != null && kind != IntentKind.CREATE_AUTOMATION) {
            return TimeReference.In(normalized, bareDuration) to bareDuration
        }
        return null to null
    }

    /** The candidate span plus the same span cut at each clause boundary, precise variants first. */
    private fun timeVariants(candidate: String): List<String> {
        val variants = ArrayList<String>(4)
        var cutIndex = candidate.length
        for (cut in TIME_CUTS) {
            val index = candidate.indexOf(cut)
            if (index in 2 until cutIndex) cutIndex = index
        }
        if (cutIndex < candidate.length) {
            variants += candidate.substring(0, cutIndex).trim()
        }
        variants += candidate
        return variants.filter { it.isNotBlank() }.distinct()
    }

    /**
     * Name the slot this intent needs but did not get.
     *
     * Returning a note rather than a default is deliberate: a missing app name must produce
     * "which app?" and never "opened the first app in the list".
     */
    private fun missingSlotNote(kind: IntentKind, entities: List<Entity>): List<String> {
        fun has(vararg kinds: EntityKind) = kinds.any { kind -> entities.any { it.kind == kind } }

        return when (kind) {
            IntentKind.OPEN_APP ->
                if (has(EntityKind.APP_NAME)) emptyList() else listOf("no app name given")
            IntentKind.START_TIMER ->
                if (has(EntityKind.DURATION, EntityKind.NUMBER, EntityKind.TIME)) emptyList()
                else listOf("no timer length given")
            IntentKind.CREATE_REMINDER, IntentKind.CREATE_ALARM ->
                if (has(EntityKind.TIME, EntityKind.DURATION, EntityKind.QUERY)) emptyList()
                else listOf("no reminder time or content given")
            IntentKind.REMEMBER ->
                if (has(EntityKind.NOTE_BODY, EntityKind.QUERY, EntityKind.TOPIC)) emptyList()
                else listOf("nothing to remember was specified")
            IntentKind.FORGET_TOPIC ->
                if (has(EntityKind.TOPIC)) emptyList() else listOf("no topic to forget was specified")
            IntentKind.QUERY_MEMORY ->
                if (has(EntityKind.TOPIC, EntityKind.QUERY)) emptyList()
                else listOf("no memory topic was specified")
            IntentKind.RENAME_ASSISTANT ->
                if (has(EntityKind.ASSISTANT_NAME)) emptyList() else listOf("no new name given")
            IntentKind.CHANGE_SETTING ->
                if (has(EntityKind.SETTING)) emptyList() else listOf("no setting was specified")
            IntentKind.TYPE_TEXT ->
                if (has(EntityKind.MESSAGE_BODY)) emptyList() else listOf("no text to type was given")
            IntentKind.SEND_MESSAGE ->
                if (has(EntityKind.CONTACT, EntityKind.RECIPIENT)) emptyList()
                else listOf("no recipient was specified")
            IntentKind.INGEST_KNOWLEDGE ->
                if (has(EntityKind.DOCUMENT)) emptyList() else listOf("no document was identified")
            else -> emptyList()
        }
    }
}
