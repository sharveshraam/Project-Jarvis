package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.util.Text
import kotlin.math.max

/**
 * Deterministic, offline intent recognition.
 *
 * ## Why patterns rather than a neural classifier
 *
 * Intent classification has to work on first launch with no download, no network and no on-device
 * model, and it has to be *explainable*: when Jarvis acts on the wrong intent the user must be able
 * to see which phrase triggered it. Every [RuleHit] therefore carries a [RuleHit.reason] naming the
 * matched text. The neural slot ([dev.jarvis.core.ports.AiPort]) may re-rank these candidates, but
 * it can never invent an intent that no rule recognised — that is what keeps a mis-parse from
 * silently becoming an action.
 *
 * ## How scoring works
 *
 * Each rule contributes a base confidence plus a small bonus per extra matching signal. Rules are
 * deliberately narrow: a rule that matches too much is worse than no rule, because a confident
 * wrong intent gets executed without asking. When the top two candidates are within
 * [AMBIGUITY_MARGIN] the caller is expected to ask instead of guessing.
 *
 * ## Ordering
 *
 * Precedence comes from confidence then [Rule.priority], never from list position. Priorities
 * encode specificity: FORGET_ALL outranks FORGET_TOPIC, which outranks QUERY_MEMORY, which
 * outranks REMEMBER — so "forget everything you know" is never read as "remember this".
 */
internal object IntentRules {

    /** Per extra matching signal. Small, so volume of matches can never manufacture certainty. */
    private const val SIGNAL_BONUS = 0.03

    /** Bonus when the user opened with the wake word or nickname. */
    private const val ADDRESS_BONUS = 0.06

    /** Bonus when the rule found a concrete entity, i.e. the request is specific enough to act on. */
    private const val ENTITY_BONUS = 0.04

    /** Two candidates within this margin are "ambiguous" and must be confirmed, not guessed. */
    const val AMBIGUITY_MARGIN = 0.08

    private data class Rule(
        val kind: IntentKind,
        val base: Double,
        /** At least one of these must match. */
        val any: List<String> = emptyList(),
        /** All of these must match too. */
        val all: List<String> = emptyList(),
        /** None of these may match - separates look-alike phrasings. */
        val not: List<String> = emptyList(),
        /** Tie-breaker when confidences are equal; higher wins. */
        val priority: Int = 0,
    )

    /**
     * An interrogative opener means the sentence is a question, not a command.
     *
     * Without this, "can you read my screen" is read as READ_SCREEN and "which app opens pdfs" as
     * OPEN_APP - answering a capability question by performing the action it asks about.
     *
     * The lookahead keeps negations out: "do not disturb" and "don't remember this" start with a
     * word that looks interrogative but is an imperative.
     */
    private const val QUESTION_OPENER =
        "^\\s*(can|could|will|would|should|shall|is|are|was|were|am|did|does|do|may|might" +
            "|what|which|who|whom|whose|how|why|when|where)\\b(?!\\s*(not|n't))"

    /**
     * The modal half of [QUESTION_OPENER] on its own.
     *
     * "Can you read my screen?" asks about ability, while "What's on the screen?" asks for the
     * action. Only the modal form is excluded for intents that are legitimately phrased as
     * questions ([QUESTION_ASKABLE]).
     */
    private const val MODAL_OPENER =
        "^\\s*(can|could|will|would|should|shall|is|are|was|were|am|did|does|do|may|might)\\b(?!\\s*(not|n't))"

    /**
     * Intents that are commands in effect but are usually spoken as questions.
     *
     * READ_SCREEN is the clear case: nobody says "read screen", they say "what's on the screen?".
     */
    private val QUESTION_ASKABLE = setOf(IntentKind.READ_SCREEN)

    /** Intents that describe an action, so they must not fire on a question about that action. */
    private val IMPERATIVE_KINDS = setOf(
        IntentKind.OPEN_APP, IntentKind.SEARCH_IN_APP, IntentKind.OPEN_URL, IntentKind.READ_SCREEN,
        IntentKind.TAP_ELEMENT, IntentKind.SELECT_ORDINAL, IntentKind.TYPE_TEXT, IntentKind.SCROLL,
        IntentKind.GO_BACK, IntentKind.GO_HOME,
        IntentKind.MEDIA_PLAY, IntentKind.MEDIA_PAUSE, IntentKind.MEDIA_RESUME, IntentKind.MEDIA_NEXT,
        IntentKind.MEDIA_PREVIOUS, IntentKind.MEDIA_SKIP, IntentKind.MEDIA_STOP, IntentKind.SET_VOLUME,
        IntentKind.CHANGE_SETTING,
        IntentKind.SEND_MESSAGE, IntentKind.REPLY_TO_LATEST, IntentKind.READ_MESSAGES,
        IntentKind.READ_NOTIFICATIONS, IntentKind.MAKE_CALL,
        IntentKind.REMEMBER, IntentKind.DO_NOT_REMEMBER, IntentKind.FORGET_TOPIC, IntentKind.FORGET_ALL,
        IntentKind.CREATE_REMINDER, IntentKind.CREATE_ALARM, IntentKind.START_TIMER,
        IntentKind.CANCEL_TIMER, IntentKind.CREATE_TASK, IntentKind.COMPLETE_TASK, IntentKind.CREATE_NOTE,
        IntentKind.CREATE_AUTOMATION, IntentKind.TOGGLE_AUTOMATION, IntentKind.DELETE_AUTOMATION,
        IntentKind.TEACH, IntentKind.INGEST_KNOWLEDGE, IntentKind.RENAME_ASSISTANT,
    )

    private val compiled: List<Pair<Rule, CompiledRule>> =
        rules().map { rule ->
            val guard = when (rule.kind) {
                in QUESTION_ASKABLE -> listOf(MODAL_OPENER)
                in IMPERATIVE_KINDS -> listOf(QUESTION_OPENER)
                else -> emptyList()
            }
            val effective = if (guard.isEmpty()) rule else rule.copy(not = rule.not + guard)
            effective to CompiledRule(
                any = effective.any.map { Regex(it) },
                all = effective.all.map { Regex(it) },
                not = effective.not.map { Regex(it) },
            )
        }

    private class CompiledRule(val any: List<Regex>, val all: List<Regex>, val not: List<Regex>)

    /**
     * Score every rule against [text], best first.
     *
     * A leading wake word is stripped first, so the table never has to care whether the user said
     * "open settings" or "hey jarvis open settings". Patterns run against the normalised text (for
     * word matching) *and* against the raw lowercased text, because normalising folds away exactly
     * the characters that carry a URL, a quoted phrase or a clock time.
     *
     * Only the strongest hit per [IntentKind] is returned: two rules for the same kind would
     * otherwise look like agreement between two intents during ambiguity checks.
     */
    fun match(text: String, config: AssistantConfig): List<RuleHit> {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return emptyList()

        val addressed = isAddressedTo(trimmed, config)
        val body = stripAddress(trimmed, config)
        if (body.isBlank()) return emptyList()

        val normalized = Text.normalize(body)
        if (normalized.isBlank()) return emptyList()
        // Raw form, lowercased only: keeps ':', '/', quotes and decimal points intact.
        val raw = body.lowercase().trim()
        val targets = if (raw == normalized) listOf(normalized) else listOf(normalized, raw)

        val best = LinkedHashMap<IntentKind, RuleHit>()

        for ((rule, patterns) in compiled) {
            if (patterns.not.any { regex -> targets.any { regex.containsMatchIn(it) } }) continue
            if (patterns.all.any { regex -> targets.none { regex.containsMatchIn(it) } }) continue

            // Each pattern counts once, whichever form of the text it matched.
            val matched = patterns.any.mapNotNull { regex ->
                targets.firstNotNullOfOrNull { target -> regex.find(target)?.value }
            }
            val allMatched = patterns.all.mapNotNull { regex ->
                targets.firstNotNullOfOrNull { target -> regex.find(target)?.value }
            }
            if (rule.any.isNotEmpty() && matched.isEmpty()) continue
            if (rule.any.isEmpty() && rule.all.isEmpty()) continue

            val signals = matched + allMatched
            var confidence = rule.base + SIGNAL_BONUS * max(0, signals.size - 1)
            if (addressed) confidence += ADDRESS_BONUS

            val entities = EntityExtractor.extract(raw, rule.kind, config)
            if (entities.isNotEmpty()) confidence += ENTITY_BONUS

            val hit = RuleHit(
                kind = rule.kind,
                confidence = confidence.coerceIn(0.0, 0.99),
                reason = describeMatch(signals),
                priority = rule.priority,
                matchedEntities = entities,
            )
            val existing = best[rule.kind]
            if (existing == null || hit.isStrongerThan(existing)) best[rule.kind] = hit
        }

        return best.values.sortedWith(
            compareByDescending<RuleHit> { it.confidence }.thenByDescending { it.priority },
        )
    }

    private fun describeMatch(signals: List<String>): String {
        if (signals.isEmpty()) return "no signal"
        val primary = signals.maxByOrNull { it.length } ?: signals.first()
        val quoted = Text.truncate(primary.trim(), 40)
        return if (signals.size == 1) "matched \"$quoted\""
        else "matched \"$quoted\" +${signals.size - 1} more signal(s)"
    }

    /** True when the user opened the request with the wake word or the assistant's nickname. */
    fun isAddressedTo(text: String, config: AssistantConfig): Boolean =
        addressPrefixes(config).any { prefix ->
            val normalized = Text.normalize(text)
            normalized == prefix ||
                normalized.startsWith("$prefix ") ||
                normalized.startsWith("$prefix,") ||
                normalized.contains(" $prefix,") ||
                normalized.contains(" $prefix ")
        }

    /**
     * Remove a leading wake word or nickname so what remains is the actual request.
     *
     * The remainder is returned with its original punctuation and casing, because that is where a
     * URL, a quoted phrase or "14:30" survives. Returns the trimmed input unchanged when no address
     * prefix is present, so callers can always use the result.
     */
    fun stripAddress(text: String, config: AssistantConfig): String {
        val trimmed = text.trim()
        val lower = trimmed.lowercase()
        for (prefix in addressPrefixes(config)) {
            if (lower == prefix) return ""
            if (lower.startsWith("$prefix ") || lower.startsWith("$prefix,") ||
                lower.startsWith("$prefix.") || lower.startsWith("$prefix!")
            ) {
                val rest = trimmed.substring(prefix.length)
                    .removePrefix(",").removePrefix(".").removePrefix("!").trim()
                if (rest.isNotEmpty()) return rest
            }
        }
        return trimmed
    }

    /**
     * Address prefixes, longest first so "hey jarvis" is stripped before "jarvis".
     *
     * The wake word and nickname are usually the same string but need not be: renaming the
     * assistant updates both, while a custom wake word may be set separately.
     */
    private fun addressPrefixes(config: AssistantConfig): List<String> {
        val names = linkedSetOf<String>()
        names += Text.normalize(config.wakeWord)
        names += Text.normalize(config.nickname)
        names += AssistantConfig.DEFAULT_NICKNAME.lowercase()
        return names.filter { it.isNotBlank() }
            .flatMap { listOf("hey $it", "ok $it", "hello $it", "hi $it", it) }
            .distinct()
            .sortedByDescending { it.length }
    }

    // ------------------------------------------------------------------
    // The rule table.
    //
    // Grouped by domain for readability. Nothing here depends on order.
    // ------------------------------------------------------------------

    private fun rules(): List<Rule> = listOf(
        // ---- memory ---------------------------------------------------------------
        // Three look-alike phrasings that must never be confused with each other:
        //   "remember that X" / "what do you remember about X" / "forget X" / "forget everything"
        Rule(
            kind = IntentKind.FORGET_ALL,
            base = 0.88,
            any = listOf(
                "\\b(forget|delete|erase|wipe|remove|clear)\\b.{0,25}\\b(everything|all (my |your )?(memor(y|ies)|data)|what you (know|remember)|the lot)\\b",
                "\\breset your memory\\b",
                "\\bstart (over|fresh)\\b",
            ),
            priority = 24,
        ),
        Rule(
            kind = IntentKind.FORGET_TOPIC,
            base = 0.84,
            any = listOf(
                "^\\s*(forget|delete|erase|remove|drop)\\b",
                "\\bforget\\b.{0,25}\\b(that|this|about|what i (said|told)|my|the)\\b",
                "\\b(unlearn|unremember)\\b",
            ),
            // "forget everything" is FORGET_ALL, not a topic deletion; and deleting a *rule* is
            // removing an automation, not a memory.
            not = listOf(
                "\\b(everything|all my|all your|all the)\\b",
                "\\b(automations?|rules?)\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.DO_NOT_REMEMBER,
            base = 0.88,
            any = listOf(
                "\\b(don'?t|do not|never|stop)\\b.{0,10}\\b(remember|save|store|record|keep|log)\\b",
                "\\bforget this (conversation|chat|session)\\b",
                "\\bno memory\\b", "\\bincognito\\b",
            ),
            priority = 24,
        ),
        Rule(
            kind = IntentKind.LIST_MEMORIES,
            base = 0.84,
            any = listOf(
                "\\bwhat (do you|you)\\b.{0,15}\\b(remember|know)\\b.{0,15}\\babout me\\b",
                "\\b(list|show|read)\\b.{0,15}\\b(my |all )?memor(y|ies)\\b",
                "\\bwhat have you (remembered|stored|learned) about me\\b",
                "\\btell me what you know\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.QUERY_MEMORY,
            base = 0.80,
            any = listOf(
                "\\b(do you|you)\\b.{0,12}\\bremember\\b",
                "\\bwhat\\b.{0,25}\\b(remember|know)\\b.{0,15}\\b(about|of)\\b",
                "\\bwhat did (we|i|you)\\b.{0,30}\\b(talk|talked|discuss|say|said|decide)\\b",
                "\\brecall\\b",
                "\\b(any|what)\\b.{0,15}\\b(memory|memories)\\b.{0,15}\\b(about|of|on)\\b",
            ),
            // "remember that ..." is a write, not a read.
            not = listOf("^\\s*(remember|note)\\b", "^\\s*(forget|delete)\\b"),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.REMEMBER,
            base = 0.84,
            any = listOf(
                "^\\s*(remember|note|memorize|memorise|keep in mind|store)\\b",
                "\\b(remember|note|memorize|memorise) that\\b",
                "\\b(don'?t|do not) forget\\b",
                "\\bsave (this|that)\\b.{0,20}\\b(memory|for later|to remember)\\b",
                "\\bmake a mental note\\b",
            ),
            priority = 22,
        ),
        Rule(
            kind = IntentKind.SET_PREFERENCE,
            base = 0.86,
            any = listOf(
                "\\b(i |we )?prefer(s|red)?\\b",
                "\\bmy preference\\b",
                "\\bi (always|usually|never|generally)\\b.{1,40}\\b(want|like|prefer|use|need)\\b",
                "\\bset (my|the) preference\\b",
                "\\bi like (my|the|it)\\b",
            ),
            priority = 18,
        ),

        // ---- time-based actions: reminder vs timer vs alarm vs task ------------------
        // A reminder carries a task; a timer is just a countdown; an alarm wakes the user.
        Rule(
            kind = IntentKind.CREATE_REMINDER,
            base = 0.86,
            any = listOf(
                "\\bremind (me|us)\\b",
                "\\b(set|create|add|make)\\b.{0,15}\\ba? ?reminder\\b",
                "\\breminder (for|to|about)\\b",
                "\\bdon'?t let me forget\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.CREATE_ALARM,
            base = 0.85,
            any = listOf(
                "\\b(wake me|wake us|get me up)\\b",
                "\\b(set|create|add)\\b.{0,12}\\ban? ?alarm\\b",
                "\\balarm (for|at|tomorrow)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.START_TIMER,
            base = 0.86,
            any = listOf(
                "\\b(set|start|put on)\\b.{0,12}\\ba? ?timer\\b",
                "\\btimer (for|of)\\b",
                "\\bcount ?down\\b",
                "^\\s*timer\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.CANCEL_TIMER,
            base = 0.84,
            any = listOf(
                "\\b(cancel|stop|clear|kill|delete)\\b.{0,15}\\b(timers?|countdowns?|alarms?|reminders?)\\b",
                "\\bsnooze\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.CREATE_TASK,
            base = 0.84,
            any = listOf(
                "\\b(add|create|put|make)\\b.{0,30}\\b(to ?-? ?do|task|my list|todo list)\\b",
                "\\badd\\b.{1,50}\\bto my (list|tasks)\\b",
                "\\bi (need|have) to\\b.{2,60}\\b(later|today|tomorrow|this week|soon)\\b",
                "\\bnew task\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.COMPLETE_TASK,
            base = 0.84,
            any = listOf(
                "\\b(mark|set)\\b.{0,25}\\b(as )?(done|complete|completed|finished)\\b",
                "\\bi (finished|completed|did|have done)\\b",
                "\\b(check|tick) (off|it off)\\b",
                "\\btask\\b.{0,15}\\b(done|complete|finished)\\b",
                "\\bdone with\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.SUMMARIZE_TASKS,
            base = 0.84,
            any = listOf(
                "\\b(summari[sz]e|sum up|recap|review)\\b.{0,20}\\b(my )?(tasks|to ?-? ?dos|progress|day|week)\\b",
                "\\bwhat did i (get|finish) done\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.LIST_TASKS,
            base = 0.82,
            any = listOf(
                "\\b(list|show|read|what are)\\b.{0,15}\\b(my )?(tasks|to ?-? ?dos|todo list)\\b",
                "\\bmy (tasks|to ?-? ?dos)\\b",
                "\\bwhat (do i|have i) (have to|need to|got to) do\\b",
                "\\banything (left|pending)\\b",
            ),
            priority = 14,
        ),
        Rule(
            kind = IntentKind.CREATE_NOTE,
            base = 0.84,
            any = listOf(
                "\\b(take|make|write|create|save|jot)\\b.{0,10}\\ba? ?note\\b",
                "^\\s*note\\b",
                "\\bnote (that|down)\\b",
                "\\bjot (that|this|it) down\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.LIST_NOTES,
            base = 0.82,
            any = listOf(
                "\\b(list|show|read|what are|open)\\b.{0,15}\\b(my )?notes\\b",
                "\\bmy notes\\b",
            ),
            priority = 14,
        ),

        // ---- apps and on-screen actions ----------------------------------------------
        Rule(
            kind = IntentKind.OPEN_URL,
            base = 0.86,
            any = listOf(
                "\\bhttps?://\\S+",
                "\\bwww\\.[a-z0-9.-]+\\.[a-z]{2,}\\b",
                "\\b(open|go to|visit|browse to)\\b.{0,15}\\b(the )?(link|url|website|site|page)\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.OPEN_APP,
            base = 0.84,
            any = listOf(
                "^\\s*(open|launch|start|run)\\b",
                "\\b(open|launch|start|run)\\b.{0,20}\\b(the )?(app|application)\\b",
                "\\bopen up\\b",
                "\\b(take me|bring up)\\b.{0,20}\\b(app|settings|camera|maps)?\\b",
                "\\b(open|go to|launch) settings\\b",
            ),
            // Questions are handled by QUESTION_OPENER, which every imperative rule gets.
            not = listOf("\\bwhat (does|is|are)\\b"),
            priority = 12,
        ),
        Rule(
            kind = IntentKind.SEARCH_IN_APP,
            base = 0.82,
            any = listOf(
                "\\bsearch\\b.{1,50}\\b(in|inside|on|using|through)\\b.{0,25}\\b(app|youtube|maps|play store|settings|chrome|gallery|files|whatsapp|telegram)\\b",
                "\\b(search|look) (for|up)\\b.{1,40}\\bin\\b.{0,20}\\b(app|youtube|maps|settings|chrome|gallery|files)\\b",
                "\\bsearch (in|inside)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.READ_SCREEN,
            base = 0.84,
            any = listOf(
                "\\b(read|describe|summari[sz]e|see|look at|tell me about|explain)\\b.{0,20}\\b(the |this )?screen\\b",
                "\\bwhat'?s on (the |this )?(screen|page)\\b",
                "\\bwhat (am i|is|are)\\b.{0,15}\\b(looking at|on screen|on the screen)\\b",
                "\\bwhat does (this|the) (screen|page) say\\b",
                "\\bread (this|the) (page|screen|text) (to me|out)?\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.SELECT_ORDINAL,
            base = 0.86,
            any = listOf(
                "^\\s*(the )?(first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|last)\\s*(one)?\\s*[.!]*$",
                "\\b(tap|click|press|select|choose|open|pick)\\b.{0,10}\\b(the )?(first|second|third|fourth|fifth|last|[0-9]+(st|nd|rd|th))\\b",
                "\\bnumber [0-9]+\\b",
                "\\bthe ([0-9]+(st|nd|rd|th))\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.TAP_ELEMENT,
            base = 0.80,
            any = listOf(
                "\\b(click|tap|press|hit|select|choose)\\b.{0,30}\\b(the |that |this )?(button|icon|link|option|item|field|checkbox|tab|menu|card)\\b",
                "^\\s*(click|tap|press|select|choose)\\b",
                "\\b(tap|click|press) (on|into)\\b",
            ),
            // "press play" is media control, not a screen node.
            not = listOf("\\b(press|hit) (play|pause|stop|next|previous)\\b"),
            priority = 12,
        ),
        Rule(
            kind = IntentKind.TYPE_TEXT,
            base = 0.82,
            any = listOf(
                "\\b(type|write|enter|input|fill in|dictate)\\b.{0,25}\\b(text|this|that|message|password|into|the field|my name)\\b",
                "\\b(type|write|enter|say)\\b.{0,8}[\"'].{1,120}[\"']",
                "^\\s*(type|write|enter)\\b",
            ),
            priority = 12,
        ),
        Rule(
            kind = IntentKind.SCROLL,
            base = 0.86,
            any = listOf(
                "\\bscroll\\b",
                "\\bswipe (up|down|left|right)\\b",
                "\\b(page|screen) (up|down)\\b",
                "\\b(show|load) more\\b",
                "\\bkeep (going|scrolling) down\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.GO_BACK,
            base = 0.88,
            any = listOf(
                "^\\s*(go back|back|back out|previous screen)\\s*[.!]*$",
                "\\bgo back\\b",
                "\\bback out (of )?(this|that|it|the app)?\\b",
                "\\breturn to the previous\\b",
            ),
            // "go back a song" is media.
            not = listOf("\\bgo back a (song|track|video)\\b"),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.GO_HOME,
            base = 0.86,
            any = listOf(
                "^\\s*(go home|home|home screen|go to home)\\s*[.!]*$",
                "\\bgo (to )?(the )?home( screen)?\\b",
                "\\b(take me|back) (to )?home\\b",
            ),
            priority = 18,
        ),

        // ---- media ------------------------------------------------------------------
        Rule(
            kind = IntentKind.MEDIA_PAUSE,
            base = 0.84,
            any = listOf("\\bpause\\b", "\\bhold (the )?(music|video|playback)\\b"),
            priority = 14,
        ),
        Rule(
            kind = IntentKind.MEDIA_RESUME,
            base = 0.84,
            any = listOf("\\bresume\\b", "\\bunpause\\b", "\\bcontinue (playing|the video|the song)\\b"),
            priority = 14,
        ),
        Rule(
            kind = IntentKind.MEDIA_STATUS,
            base = 0.86,
            any = listOf(
                "\\bwhat'?s playing\\b", "\\bwhat (song|track|video) is this\\b",
                "\\bwho (sings|is singing|sang)\\b", "\\bwhich (song|track|album)\\b",
                "\\bname of (this|the) (song|track)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.MEDIA_SKIP,
            base = 0.86,
            any = listOf(
                "\\bskip (forward|back|ahead|backwards?)\\b",
                "\\b(fast ?forward|rewind)\\b",
                "\\bskip\\b.{0,10}\\b([0-9]+|a few) (seconds|minutes)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.MEDIA_NEXT,
            base = 0.86,
            any = listOf(
                "\\b(next|skip) (track|song|video|one|episode)\\b",
                "^\\s*(next|skip)\\s*[.!]*$",
                "\\bplay the next\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.MEDIA_PREVIOUS,
            base = 0.86,
            any = listOf(
                "\\bprevious (track|song|video|one)\\b",
                "\\bgo back a (song|track|video)\\b",
                "\\bplay the (last|previous) (song|track)\\b",
                "\\breplay (this|the song)\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.MEDIA_STOP,
            base = 0.84,
            any = listOf(
                "\\bstop\\b.{0,12}\\b(the )?(music|playback|video|song|audio|stream)\\b",
                "\\b(turn|switch) off (the )?(music|audio|player)\\b",
            ),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.MEDIA_PLAY,
            base = 0.78,
            any = listOf(
                // The object is required: a bare "play" would otherwise out-score the more
                // specific "play the previous song".
                "\\bplay\\b.{0,30}\\b(song|songs|music|track|video|album|playlist|movie|podcast|something|it)\\b",
                "^\\s*play\\s*[.!]*$",
                "\\b(put|start)\\b.{0,15}\\b(music|a song|something) on\\b",
                "\\b(press|hit) (play|start)\\b",
            ),
            // "play" inside "display"/"played by" should not fire.
            not = listOf("\\b(display|player settings)\\b"),
            priority = 10,
        ),
        Rule(
            kind = IntentKind.SET_VOLUME,
            base = 0.84,
            any = listOf(
                "\\bvolume\\b",
                "\\b(louder|quieter|softer|too loud|too quiet)\\b",
                "\\b(turn|set|put)\\b.{0,12}\\b(it|the sound|the music|the volume)?\\b.{0,8}\\b(up|down)\\b",
                "\\b(mute|unmute)\\b",
            ),
            not = listOf("\\bvolume of (the|my) (data|storage)\\b"),
            priority = 16,
        ),

        // ---- device settings and status ---------------------------------------------
        Rule(
            kind = IntentKind.DEVICE_STATUS,
            base = 0.84,
            any = listOf(
                "\\bwhat time\\b", "\\btime is it\\b", "\\bthe time\\b",
                "\\bwhat('?s| is) (the |today'?s )?date\\b", "\\bwhat day is it\\b", "\\btoday'?s date\\b",
                "\\bbattery\\b", "\\b(charging|charge level|power level)\\b",
                "\\b(storage|disk space|free space|space left|memory available)\\b",
                "\\b(am i|are we) (online|connected)\\b", "\\bnetwork status\\b",
                "\\bhow'?s my (phone|device)\\b", "\\bdevice (status|info)\\b", "\\bphone status\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.CHANGE_SETTING,
            base = 0.82,
            any = listOf(
                "\\b(torch|flashlight|flash light)\\b",
                "\\b(wi ?-?fi|wireless|bluetooth|airplane mode|flight mode|hotspot|mobile data)\\b",
                "\\b(do not disturb|dnd|quiet (mode|hours))\\b",
                "\\b(brightness|brighter|dimmer)\\b",
                "\\b(auto ?-?rotate|rotation|landscape|portrait)\\b",
                "\\b(silent mode|vibrate only|ring mode|vibration mode)\\b",
                "\\b(screen|display) (on|off|timeout)\\b",
                "\\block\\b.{0,12}\\b(phone|device|screen)\\b",
                "\\b(turn|switch|toggle|enable|disable)\\b.{0,15}\\b(on|off)\\b.{0,20}\\b(torch|wi ?-?fi|bluetooth|data|location|gps|nfc|dnd|night light|dark mode|battery saver)\\b",
                "\\b(dark mode|night light|battery saver|power saving|location|gps|nfc)\\b",
            ),
            // "I prefer dark mode" is a fact about the user, not a toggle to flip now; and a
            // conditional ("whenever ... turn on wifi") is an automation, not an immediate change.
            not = listOf(
                "\\bprefer(s|red)?\\b", "\\bpreference\\b",
                "\\b(whenever|every time|each time|any time)\\b",
            ),
            priority = 16,
        ),

        // ---- assistant configuration -------------------------------------------------
        Rule(
            kind = IntentKind.RENAME_ASSISTANT,
            base = 0.90,
            any = listOf(
                "\\bcall you\\b", "\\byour name is\\b",
                "\\brename (yourself|you)\\b", "\\bchange your name\\b",
                "\\b(from now on|now)\\b.{0,10}\\b(you'?re|you are|your name)\\b",
                "\\b(i'?ll|i will) call you\\b", "\\bnew name\\b",
                "\\bwhat should i call you\\b.{0,20}\\b\\?\\s*$",
            ),
            priority = 26,
        ),
        Rule(
            kind = IntentKind.AUTHENTICATE,
            base = 0.86,
            any = listOf(
                "\\b(secret (phrase|word|code)|passphrase|pass phrase)\\b",
                "\\b(authenticate|verify) (me|my identity|myself)\\b",
                "\\bunlock\\b.{0,15}\\b(private mode|your memory|the assistant|jarvis|me)\\b",
                "\\bi am the (owner|user)\\b",
            ),
            priority = 24,
        ),
        Rule(
            kind = IntentKind.ASK_CAPABILITIES,
            base = 0.84,
            any = listOf(
                "\\bwhat can you do\\b", "\\bwhat are you capable of\\b",
                "\\b(can you|could you|are you able to|do you support)\\b",
                "\\b(list|show)\\b.{0,12}\\b(your )?(skills|abilities|capabilities|features|commands)\\b",
                "\\byour capabilities\\b",
                "\\bwhy can'?t you\\b",
                "\\b(is|are)\\b.{0,30}\\b(supported|possible|available)\\b",
                "^\\s*help\\b", "\\bi need help\\b", "\\bhow (do|can) i (use|talk to)\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.PRIVACY_CONTROL,
            base = 0.84,
            any = listOf(
                "\\b(export|download|back ?up|give me a copy of)\\b.{0,20}\\b(my data|all data|the data|my memor(y|ies)|everything)\\b",
                "\\b(delete|erase|wipe)\\b.{0,20}\\b(my data|all (my )?data|my history)\\b",
                "\\b(are you|is it|have you been) (listening|recording|watching|uploading|tracking)\\b",
                "\\bwhat (are you|do you|is being)\\b.{0,12}\\b(collect|collecting|store|storing|save|saving|record|recording|send|sending)\\b",
                "\\bprivacy\\b", "\\bwhere (is|does) my data\\b", "\\bis my data (safe|local|private)\\b",
                "\\b(what|which) permissions?\\b", "\\bpermission (status|list)\\b",
                "\\b(disable|turn off|stop)\\b.{0,15}\\b(memory|tracking|analytics|data collection|logging|telemetry)\\b",
            ),
            priority = 22,
        ),

        // ---- teaching and knowledge ---------------------------------------------------
        Rule(
            kind = IntentKind.TEACH,
            base = 0.86,
            any = listOf(
                "\\bteach me\\b", "\\b(start|enter|begin) teaching( mode)?\\b", "\\bteaching mode\\b",
                "\\blet me teach you\\b", "\\bi want to teach you\\b",
                "\\blearn (this|about|how to|from me)\\b", "\\bteach yourself\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.CONTINUE_LESSON,
            base = 0.80,
            any = listOf(
                "\\b(continue|resume|next)\\b.{0,12}\\b(the )?(lesson|quiz|topic|chapter|course)\\b",
                "\\bnext lesson\\b", "\\bkeep going\\b", "\\bgo on\\b",
                "\\bcontinue where we (left off|stopped)\\b",
            ),
            priority = 14,
        ),
        Rule(
            kind = IntentKind.ANSWER_QUESTION,
            base = 0.60,
            any = listOf(
                "\\b(the answer is|my answer is|i think (it'?s|the answer))\\b",
                "^\\s*(a|b|c|d|true|false)\\s*[.!]*$",
            ),
            priority = 8,
        ),
        Rule(
            kind = IntentKind.LEARNING_PROGRESS,
            base = 0.84,
            any = listOf(
                "\\b(learning|lesson)\\b.{0,12}\\b(progress|status)\\b",
                "\\bmy progress\\b", "\\bhow (am i|well am i) doing\\b",
                "\\bhow much have (i|you) (learned|progressed)\\b",
                "\\bwhat have (i|you) learned\\b", "\\bprogress (report|update|so far)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.INGEST_KNOWLEDGE,
            base = 0.84,
            any = listOf(
                "\\b(import|ingest|index|add|read|learn from|upload)\\b.{0,25}\\b(pdf|document|documents|file|files|notes?|book|article|ebook)\\b",
                "\\b(this|the|my) (pdf|document|file|book|article)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.QUERY_KNOWLEDGE,
            base = 0.84,
            any = listOf(
                "\\b(search|find|look up|check)\\b.{0,25}\\b(my notes|my documents|my knowledge|the knowledge base|the pdf|my pdfs?)\\b",
                "\\baccording to\\b.{0,20}\\b(the |my )?(pdf|document|notes?|book|article)\\b",
                "\\bwhat (does|do) the (pdf|document|book|notes?) say\\b",
                "\\bin my (notes|documents|knowledge base)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.EXPLAIN,
            base = 0.76,
            any = listOf(
                "^\\s*(explain|define|why|how does|how do|how is|what is|what are|what'?s)\\b",
                "\\bexplain\\b", "\\bwhat does\\b.{1,50}\\bmean\\b",
                "\\b(meaning|definition) of\\b", "\\bsynonym\\b", "\\bantonym\\b",
                "\\bwhy (does|do|is|are|did|would|can)\\b",
                "\\bhow (does|do|is|are|was|were)\\b.{2,50}\\bwork\\b",
                "\\b(tell|teach) me about\\b",
            ),
            priority = 10,
        ),

        // ---- automation ---------------------------------------------------------------
        Rule(
            kind = IntentKind.CREATE_AUTOMATION,
            base = 0.86,
            any = listOf(
                "\\b(whenever|every time|each time|any time|automatically)\\b",
                "\\bwhen(ever)?\\b.{5,120}\\b(then |please |just |automatically )?\\b(open|send|turn|set|remind|notify|start|mute|unmute|enable|disable|play|pause|tell|read|show|lock|silence)\\b",
                "\\bif\\b.{5,120}\\b(then|please|just|automatically)\\b",
                "\\b(create|add|set up|make|new)\\b.{0,15}\\b(a |an )?(rule|automation|trigger)\\b",
                "\\bautomat(e|ion)\\b",
                "\\bwhen i\\b.{3,90}\\b(do|don'?t|do not|arrive|leave|plug|unplug|sleep|wake|connect|disconnect|open|start|stop)\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.LIST_AUTOMATIONS,
            base = 0.84,
            any = listOf(
                "\\b(list|show|what|read|how many)\\b.{0,20}\\b(my )?(automations?|rules?)\\b",
                "\\bmy automations\\b", "\\bwhat (rules|automations) (do i have|are active)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.TOGGLE_AUTOMATION,
            base = 0.84,
            any = listOf(
                "\\b(enable|disable|pause|resume|activate|deactivate|turn (on|off))\\b.{0,25}\\b(the )?(automation|rule)s?\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.DELETE_AUTOMATION,
            base = 0.86,
            any = listOf(
                "\\b(delete|remove|cancel|clear|drop)\\b.{0,20}\\b(the |my |all )?(automations?|rules?)\\b",
            ),
            priority = 22,
        ),

        // ---- messaging and calls (recognised so the limitation can be explained) -------
        Rule(
            kind = IntentKind.REPLY_TO_LATEST,
            base = 0.84,
            any = listOf(
                "\\breply\\b.{0,20}\\b(to )?(that|him|her|them|it|the (last|latest)|this message)\\b",
                "^\\s*reply\\b", "\\banswer (that|him|her|them|the message)\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.SEND_MESSAGE,
            base = 0.84,
            any = listOf(
                "\\b(send|text|message|whatsapp|sms|ping)\\b.{0,30}\\b(a |the )?(message|text|sms|note|to|mom|mum|dad|her|him|them|my)\\b",
                "\\btext (mom|mum|dad|her|him|them|my|back)\\b",
                "\\bsend (a )?(whatsapp|sms|text|message)\\b",
                // "text Rahul" - the verb alone is enough; the recipient is the next word.
                "^\\s*(text|message|sms|whatsapp|ping)\\b",
            ),
            not = listOf("\\btype text\\b"),
            priority = 16,
        ),
        Rule(
            kind = IntentKind.READ_MESSAGES,
            base = 0.84,
            any = listOf(
                "\\b(read|show|check|list|any|unread|new)\\b.{0,15}\\b(messages|texts|sms|emails?)\\b",
                "\\bunread messages\\b", "\\bwhat did\\b.{1,25}\\b(say|text|message)\\b",
                "\\b(any|new) messages\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.READ_NOTIFICATIONS,
            base = 0.84,
            any = listOf(
                "\\b(read|show|check|list|any|unread|new|clear|dismiss)\\b.{0,15}\\b(notifications?|alerts)\\b",
                "\\bnotification (shade|panel|drawer)\\b", "\\bpull down notifications\\b",
            ),
            priority = 18,
        ),
        Rule(
            kind = IntentKind.MAKE_CALL,
            base = 0.84,
            any = listOf(
                // The verb needs an object. A bare "\bphone\b" would otherwise match "lock my
                // phone", "phone battery" and every other sentence that mentions the device.
                "\\b(call|phone|ring up|dial)\\s+(mom|mum|mummy|dad|papa|her|him|them|my|a|the|back|\\d[\\d -]{5,})\\b",
                "^\\s*(call|dial)\\b",
                "\\bmake a (call|phone call)\\b",
                "\\bgive (me |him |her )?a (call|ring)\\b",
            ),
            // Idioms, not telephony.
            not = listOf(
                "\\bwhat (do you|you) call\\b", "\\bcall (it|that|me|this)\\b",
                "\\bi (would|will|can|shall) call you\\b", "\\bcalled\\b",
                "\\bcall (it|them) (a day|off)\\b",
            ),
            priority = 16,
        ),

        // ---- offline computation -------------------------------------------------------
        Rule(
            kind = IntentKind.CONVERT_UNITS,
            base = 0.86,
            any = listOf(
                "\\bconvert\\b",
                "\\bhow many\\b.{1,30}\\b(in|is|are|to)\\b",
                "[0-9]\\b.{0,12}\\b(km|kilomet(er|re)s?|miles?|mi|kg|kilograms?|grams?|lbs?|pounds?|ounces?|celsius|fahrenheit|degrees?|inches|inch|cm|centimet(er|re)s?|met(er|re)s?|lit(er|re)s?|ml|gallons?|feet|foot|usd|eur|inr|gbp|jpy)\\b.{0,12}\\b(to|in|into|is)\\b",
                "\\b(exchange rate|currency)\\b",
            ),
            priority = 22,
        ),
        Rule(
            kind = IntentKind.CALCULATE,
            base = 0.86,
            any = listOf(
                "\\b(calculate|compute|work out|add up|what'?s|how much is|whats)\\b.{0,15}[0-9].{0,15}(\\+|-|\\*|/|plus|minus|times|multiplied|divided|percent|%|squared)",
                "[0-9].{0,12}\\b(plus|minus|times|divided by|multiplied by)\\b.{0,12}[0-9]",
                "\\b[0-9]+ ?(percent|%|pc) of [0-9]+",
                "\\bsquare root\\b", "\\b[0-9]+ ?(squared|cubed)\\b",
            ),
            priority = 22,
        ),

        // ---- online (recognised, then reported as ONLINE REQUIRED) ----------------------
        Rule(
            kind = IntentKind.ASK_ONLINE_AI,
            base = 0.84,
            any = listOf(
                "\\b(ask|use|talk to|query)\\b.{0,15}\\b(the )?(online ai|cloud ai|chatgpt|gpt|gemini|claude|the internet|server ai)\\b",
                "\\bask online\\b", "\\buse the (online|cloud) (ai|model)\\b",
                "\\btranslate\\b", "\\btranslation\\b",
                "\\bgo online\\b", "\\buse ai (for|to)\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.WEB_SEARCH,
            base = 0.82,
            any = listOf(
                "\\b(search|google|look up|find out|search for)\\b.{0,25}\\b(the web|internet|online|for me)\\b",
                "^\\s*(search|google)\\b",
                "\\bweather\\b", "\\bforecast\\b", "\\btemperature outside\\b",
                "\\b(news|headlines)\\b", "\\bwhat'?s happening (in|around)\\b",
                "\\bwho (is|was|won)\\b", "\\bwhere is\\b.{1,40}\\b(located|from)\\b",
                "\\blatest (news|updates?|score|results?)\\b",
            ),
            // Naming the user's own corpus or a specific app keeps the search local: reaching for
            // the web when the answer is already on the device would be both slow and a privacy
            // regression.
            not = listOf(
                "\\bsearch (in|inside)\\b.{0,20}\\b(app|settings|chrome|youtube|maps|gallery|files)\\b",
                "\\bmy (notes|documents|knowledge base|pdfs?)\\b",
                "\\bin (chrome|youtube|maps|settings|the app|play store|gallery|files|whatsapp)\\b",
            ),
            priority = 16,
        ),

        // ---- dialogue control -----------------------------------------------------------
        // Anchored to the start of the utterance: "yes, open settings" must be read as a
        // command, not as a bare confirmation. NluEngine applies a further check that
        // dialogue intents only win when the utterance is short.
        Rule(
            kind = IntentKind.CONFIRM,
            base = 0.88,
            any = listOf(
                "^\\s*(yes|yeah|yep|yup|ya|sure|okay|ok|k|do it|go ahead|confirm|correct|right|proceed|affirmative|please do|indeed|absolutely)\\s*[.!]*$",
                "^\\s*(yes|yeah|yep|sure|okay|ok|go ahead|do it|confirm|proceed)\\b",
            ),
            priority = 28,
        ),
        Rule(
            kind = IntentKind.DENY,
            base = 0.88,
            any = listOf(
                "^\\s*(no|nope|nah|negative|don'?t|do not|don'?t do it)\\s*[.!]*$",
                "^\\s*(no|nope|nah|negative)\\b",
            ),
            not = listOf("\\bno (memory|problem|worries|idea)\\b"),
            priority = 28,
        ),
        Rule(
            kind = IntentKind.CANCEL,
            base = 0.86,
            any = listOf(
                "^\\s*(cancel|stop|abort|never ?mind|forget it|quit|hold on|wait)\\s*[.!]*$",
                "\\bstop (that|it|what you'?re doing|now)\\b",
                "\\bcancel (that|it|this|the request|everything)\\b",
                "\\bnever ?mind\\b", "\\bforget (about )?it\\b",
            ),
            priority = 28,
        ),
        Rule(
            kind = IntentKind.REPEAT,
            base = 0.86,
            any = listOf(
                "\\b(repeat|say)\\b.{0,10}\\b(that|it)\\b",
                "\\bwhat did you (say|just say)\\b", "\\bsay (that )?again\\b",
                "^\\s*(repeat|again|come again|pardon|sorry)\\s*[.?]*$",
                "\\bcan you repeat\\b", "\\bi didn'?t (catch|hear) that\\b",
            ),
            priority = 20,
        ),
        Rule(
            kind = IntentKind.THANKS,
            base = 0.88,
            any = listOf(
                "\\b(thanks|thank you|thx|cheers|much appreciated|i appreciate it|much obliged)\\b",
            ),
            priority = 14,
        ),
        Rule(
            kind = IntentKind.GREETING,
            base = 0.86,
            any = listOf(
                // End-anchored: a greeting followed by a request is a request.
                "\\b(hi|hey|hello|yo|howdy|namaste|hiya)\\b\\s*(there|you)?\\s*[.!]*$",
                "^\\s*good (morning|afternoon|evening|night)\\b",
                "\\bgood (morning|afternoon|evening)\\b.{0,12}$",
            ),
            priority = 10,
        ),
        Rule(
            kind = IntentKind.CHITCHAT,
            base = 0.44,
            any = listOf(
                "\\b(tell me a joke|joke|story|sing a song|sing|i'?m bored|bored)\\b",
                "\\bhow are you\\b", "\\bwho are you\\b", "\\bwhat are you\\b",
                "\\byour name\\b", "\\bwhat do you think\\b", "\\bhow do you feel\\b",
                "\\b(bye|goodbye|good night|see you|farewell|take care|sleep now|goodnight)\\b",
                "\\b(i love you|you'?re (great|awesome|the best)|well done|good job)\\b",
            ),
            priority = 4,
        ),
    )
}

/**
 * One rule firing, with the evidence for it.
 *
 * [matchedEntities] is carried here rather than re-extracted later so the confidence boost and the
 * final [ParsedRequest] agree about what was found.
 */
internal data class RuleHit(
    val kind: IntentKind,
    val confidence: Double,
    /** Human-readable evidence, e.g. `matched "remind me"`. Shown in diagnostics and logs. */
    val reason: String,
    val priority: Int = 0,
    val matchedEntities: List<Entity> = emptyList(),
) {
    /** Confidence first, then specificity, so a narrow rule beats a broad one on a tie. */
    fun isStrongerThan(other: RuleHit): Boolean =
        confidence > other.confidence || (confidence == other.confidence && priority > other.priority)

    fun toCandidate(): IntentCandidate = IntentCandidate(kind, confidence, reason)
}
