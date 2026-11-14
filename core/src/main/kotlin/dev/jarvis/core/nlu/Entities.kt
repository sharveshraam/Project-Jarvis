package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.util.Text

/**
 * Pulls structured arguments out of a request, offline and deterministically.
 *
 * An intent on its own is rarely actionable: OPEN_APP needs *which* app, CREATE_REMINDER needs
 * *what* and *when*, CHANGE_SETTING needs *which* setting and *to what value*. This object fills
 * those slots. Every extractor keeps both a normalised [Entity.value] for tools and the user's own
 * words in [Entity.raw], so a confirmation prompt can quote back exactly what was said.
 *
 * ## What is deliberately not done here
 *
 * - **No package resolution.** An app entity holds the name the user spoke. Mapping that to an
 *   installed package is the job of [dev.jarvis.core.ports.DevicePort], which can see the real
 *   launcher list and fuzzy-match against it. Guessing a package name in core would be fabrication.
 * - **No temporal parsing.** Time and duration entities come from [TemporalParser], which owns the
 *   clock and the user's day-part definitions. [NluEngine] merges them in.
 * - **No contact lookup.** A contact entity is the spoken name; resolving it to a number needs the
 *   contacts port and, when ambiguous, a question back to the user.
 *
 * When a slot cannot be filled the extractor returns nothing rather than a placeholder - a missing
 * entity is what makes the engine ask a follow-up question instead of acting on a guess.
 */
internal object EntityExtractor {

    fun extract(text: String, kind: IntentKind, config: AssistantConfig): List<Entity> {
        val out = ArrayList<Entity>(3)

        fun add(entity: Entity?) {
            if (entity != null && entity.value.isNotBlank() && out.none { it.kind == entity.kind }) {
                out += entity
            }
        }

        when (kind) {
            IntentKind.OPEN_APP -> add(appName(text))
            IntentKind.SEARCH_IN_APP -> {
                add(appName(text))
                add(query(text, SEARCH_IN_APP_PREFIXES))
            }
            IntentKind.OPEN_URL -> add(url(text))
            IntentKind.READ_SCREEN -> add(query(text, GENERIC_LEAD_INS))

            IntentKind.TAP_ELEMENT -> add(element(text))
            IntentKind.TYPE_TEXT -> {
                add(spokenSpan(text, EntityKind.MESSAGE_BODY))
                add(element(text))
            }
            IntentKind.SELECT_ORDINAL -> add(ordinal(text))
            IntentKind.SCROLL -> add(direction(text))

            IntentKind.SET_VOLUME, IntentKind.CHANGE_SETTING -> {
                add(setting(text))
                add(settingValue(text))
                add(level(text))
                add(percent(text))
                add(number(text))
                add(direction(text))
            }
            IntentKind.DEVICE_STATUS -> add(query(text, GENERIC_LEAD_INS))

            IntentKind.MEDIA_PLAY, IntentKind.MEDIA_STOP -> add(query(text, MEDIA_LEAD_INS))
            IntentKind.MEDIA_SKIP -> {
                add(direction(text))
                add(number(text))
            }

            IntentKind.MAKE_CALL -> add(contact(text, CALL_LEAD_INS))
            IntentKind.SEND_MESSAGE, IntentKind.REPLY_TO_LATEST -> {
                add(contact(text, MESSAGE_LEAD_INS))
                add(spokenSpan(text, EntityKind.MESSAGE_BODY))
                add(query(text, MESSAGE_LEAD_INS))
            }
            IntentKind.READ_MESSAGES, IntentKind.READ_NOTIFICATIONS -> add(contact(text, GENERIC_LEAD_INS))

            IntentKind.REMEMBER, IntentKind.CREATE_NOTE -> {
                add(spokenSpan(text, EntityKind.NOTE_BODY))
                add(query(text, REMEMBER_LEAD_INS))
            }
            IntentKind.SET_PREFERENCE -> {
                add(preferenceKey(text))
                add(preferenceValueEntity(text))
                add(spokenSpan(text, EntityKind.NOTE_BODY))
            }
            IntentKind.FORGET_TOPIC -> add(topic(text, FORGET_LEAD_INS))
            IntentKind.QUERY_MEMORY -> add(topic(text, MEMORY_QUERY_LEAD_INS))
            IntentKind.LIST_MEMORIES -> add(topic(text, MEMORY_QUERY_LEAD_INS))
            IntentKind.DO_NOT_REMEMBER -> add(topic(text, FORGET_LEAD_INS))

            IntentKind.CREATE_REMINDER, IntentKind.CREATE_ALARM -> {
                add(query(text, REMINDER_LEAD_INS))
                add(recipient(text))
            }
            IntentKind.START_TIMER -> {
                add(number(text))
                add(query(text, REMINDER_LEAD_INS))
            }
            IntentKind.CANCEL_TIMER -> add(topic(text, CANCEL_TARGET_LEAD_INS))
            IntentKind.CREATE_TASK -> add(query(text, TASK_LEAD_INS, preferLast = false))
            IntentKind.COMPLETE_TASK -> add(topic(text, COMPLETE_LEAD_INS, preferLast = false))
            IntentKind.SUMMARIZE_TASKS, IntentKind.LIST_TASKS, IntentKind.LIST_NOTES ->
                add(query(text, GENERIC_LEAD_INS))

            IntentKind.CREATE_AUTOMATION -> {
                add(automationTrigger(text))
                add(automationAction(text))
            }
            IntentKind.TOGGLE_AUTOMATION, IntentKind.DELETE_AUTOMATION, IntentKind.LIST_AUTOMATIONS -> {
                add(settingValue(text))
                add(topic(text, AUTOMATION_LEAD_INS))
            }

            IntentKind.TEACH -> add(topic(text, TEACH_LEAD_INS))
            IntentKind.CONTINUE_LESSON -> add(topic(text, TEACH_LEAD_INS))
            IntentKind.EXPLAIN -> add(query(text, EXPLAIN_LEAD_INS))
            IntentKind.LEARNING_PROGRESS -> add(topic(text, TEACH_LEAD_INS))

            IntentKind.INGEST_KNOWLEDGE -> add(document(text))
            IntentKind.QUERY_KNOWLEDGE -> add(query(text, KNOWLEDGE_LEAD_INS))

            IntentKind.RENAME_ASSISTANT -> add(assistantName(text, config))

            // AUTHENTICATE deliberately extracts nothing. The secret phrase is the user's own text,
            // and turning it into an entity would copy it into parse results, confirmations and logs
            // that outlive the moment. The auth flow reads it once, in place, from the request.
            IntentKind.AUTHENTICATE -> Unit

            IntentKind.CALCULATE -> {
                add(expression(text))
                add(percent(text))
            }
            IntentKind.CONVERT_UNITS -> {
                add(conversion(text))
                add(number(text))
                add(percent(text))
            }

            IntentKind.WEB_SEARCH, IntentKind.ASK_ONLINE_AI -> add(query(text, SEARCH_LEAD_INS))

            // Dialogue intents act on conversation state, not on the world, so they carry no
            // entities. UNKNOWN gets none either: nothing was recognised to extract from.
            else -> Unit
        }

        return out
    }

    // ------------------------------------------------------------------
    // Lead-in phrases: the words that introduce a slot and are not part of it.
    // ------------------------------------------------------------------

    private val GENERIC_LEAD_INS = listOf(
        "tell me about", "show me", "read me", "describe", "summarise", "summarize", "explain",
        "what is", "what are", "what's", "whats", "list", "show", "read", "check", "any", "my",
        "the", "please", "for me",
    )
    private val SEARCH_LEAD_INS = listOf(
        "search the web for", "search online for", "google", "search for", "search", "look up",
        "find out", "find", "what's the", "what is the", "what's", "whats", "the", "please",
    )
    private val SEARCH_IN_APP_PREFIXES = listOf(
        "search in", "search inside", "search on", "search through", "look up", "search for",
        "search", "find", "in", "the", "please",
    )
    private val MEDIA_LEAD_INS = listOf("play", "start", "put on", "resume", "stop", "the", "some", "my")
    private val CALL_LEAD_INS = listOf("call", "phone", "ring up", "dial", "make a call to", "the", "my", "please")
    private val MESSAGE_LEAD_INS = listOf(
        "send a message to", "send a text to", "send a whatsapp to", "message", "text", "whatsapp",
        "sms", "send", "reply to", "to", "the", "my", "please",
    )
    private val REMEMBER_LEAD_INS = listOf(
        "remember that", "remember", "note that", "note down", "note", "keep in mind", "store",
        "memorize", "memorise", "save this", "take a note that", "take a note", "make a note that",
        "make a note", "write a note that", "write a note", "jot down", "that", "this", "please",
    )
    private val FORGET_LEAD_INS = listOf(
        "forget about", "forget", "delete", "erase", "remove", "drop", "unlearn", "that", "the",
        "my", "what i said about", "what i told you about", "everything about", "please",
    )
    private val MEMORY_QUERY_LEAD_INS = listOf(
        "what do you remember about", "what do you know about", "do you remember", "remember about",
        "remember", "recall", "anything about", "what did we talk about", "about", "the", "my", "please",
    )
    private val REMINDER_LEAD_INS = listOf(
        "remind me to", "remind me that", "remind me about", "remind me", "set a reminder to",
        "set a reminder for", "reminder to", "reminder for", "reminder about", "reminder",
        "wake me at", "wake me to", "wake me", "set an alarm for", "set an alarm at",
        "set a timer for", "timer for", "timer of", "timer", "alarm for", "alarm at", "alarm",
        "add a task to", "add task", "task", "please",
    )
    private val TASK_LEAD_INS = listOf(
        "add", "create", "make", "put", "new task", "task", "to my list", "to my tasks",
        "to do", "todo", "i need to", "i have to", "the", "my", "a", "please",
    )
    private val COMPLETE_LEAD_INS = listOf(
        "mark", "complete", "finish", "check off", "i finished", "i completed", "i did",
        "i have done", "done with", "set", "the", "my", "a", "please",
    )
    private val CANCEL_TARGET_LEAD_INS = listOf("cancel", "stop", "clear", "kill", "delete", "the", "my", "a", "please")
    private val AUTOMATION_LEAD_INS = listOf(
        "the automation", "automation", "the rule", "rule", "automations", "rules", "enable",
        "disable", "pause", "resume", "delete", "remove", "cancel", "turn on", "turn off", "the", "my",
        "all", "please",
    )
    private val TEACH_LEAD_INS = listOf(
        "teach me", "teach", "learn", "lesson", "topic", "about", "start teaching", "teaching mode",
        "continue", "next", "the", "my", "please", "on",
    )
    private val EXPLAIN_LEAD_INS = listOf(
        "explain", "define", "what does", "what do", "what is", "what are", "what's", "whats",
        "tell me about", "meaning of", "definition of", "how does", "how do", "how is", "why does",
        "why do", "why is", "why", "mean", "the", "a", "an", "please",
    )
    private val KNOWLEDGE_LEAD_INS = listOf(
        "according to", "in my notes", "in my documents", "search my notes", "search my documents",
        "what does the pdf say", "from the", "from my", "the knowledge base", "knowledge base",
        "search", "find", "look up", "check", "the", "my", "please",
    )

    /** Trailing noise that is never part of a slot value. */
    private val TRAILING_NOISE = setOf(
        "please", "now", "thanks", "thank", "you", "for", "me", "ok", "okay", "ya", "jarvis",
        "quickly", "immediately", "right", "just", "and", "then",
    )

    // ------------------------------------------------------------------
    // Extractors
    // ------------------------------------------------------------------

    private val APP_VERBS = listOf(
        "open up", "open", "launch", "start", "run", "take me to", "bring up", "go to", "load",
    )

    /** Well-known aliases, so "insta" and "instagram" produce the same entity value. */
    private val APP_ALIASES = mapOf(
        "insta" to "instagram", "ig" to "instagram", "instagram" to "instagram",
        "yt" to "youtube", "youtube" to "youtube",
        "wa" to "whatsapp", "whats app" to "whatsapp", "whatsapp" to "whatsapp",
        "fb" to "facebook", "facebook" to "facebook",
        "gmaps" to "maps", "google maps" to "maps", "maps" to "maps",
        "gmail" to "gmail", "mail" to "gmail",
        "settings" to "settings", "camera" to "camera", "gallery" to "gallery",
        "photos" to "gallery", "clock" to "clock", "calculator" to "calculator",
        "chrome" to "chrome", "browser" to "chrome", "play store" to "play store",
        "playstore" to "play store", "files" to "files", "contacts" to "contacts",
        "phone" to "phone", "messages" to "messages", "calendar" to "calendar",
        "spotify" to "spotify", "netflix" to "netflix", "telegram" to "telegram",
    )

    fun appName(text: String): Entity? {
        Text.firstQuotedSpan(text)?.let { quoted ->
            return Entity(EntityKind.APP_NAME, canonicalApp(quoted), quoted)
        }
        for (verb in APP_VERBS) {
            val match = Regex("\\b${Regex.escape(verb)}\\b").find(text) ?: continue
            val rest = text.substring(match.range.last + 1).trim()
            val phrase = cleanSlot(rest, stopWords = APP_STOP_WORDS)
            if (phrase.isBlank() || phrase.length > 40) continue
            return Entity(EntityKind.APP_NAME, canonicalApp(phrase), phrase)
        }
        return null
    }

    private val APP_STOP_WORDS = setOf(
        "app", "application", "for", "to", "in", "on", "at", "and", "then", "with", "please",
        "now", "immediately",
    )

    /** Alias-normalised, lowercase: the port does the real fuzzy match against installed apps. */
    private fun canonicalApp(phrase: String): String {
        val cleaned = Text.normalize(phrase).trim().trim('.', ',', '!')
        return APP_ALIASES[cleaned] ?: cleaned
    }

    private val URL_REGEX = Regex("(https?://\\S+|www\\.[a-z0-9.-]+\\.[a-z]{2,}\\S*)")

    fun url(text: String): Entity? {
        val match = URL_REGEX.find(text) ?: return null
        val value = match.value.trimEnd('.', ',', '!', '?')
        val normalized = if (value.startsWith("www.")) "https://$value" else value
        return Entity(EntityKind.URL, normalized, value)
    }

    private val ORDINAL_NUMBER = Regex("\\b([0-9]+)(st|nd|rd|th)\\b")

    fun ordinal(text: String): Entity? {
        ORDINAL_NUMBER.find(text)?.let { match ->
            val n = match.groupValues[1].toIntOrNull() ?: return null
            return Entity(EntityKind.ORDINAL, n.toString(), match.value)
        }
        for (token in Text.tokenize(text)) {
            val index = Text.ordinalToIndex(token)
            if (index != null) return Entity(EntityKind.ORDINAL, index.toString(), token)
        }
        return null
    }

    private val NUMBER_REGEX = Regex("\\b([0-9]+(?:\\.[0-9]+)?)\\b")

    /** Articles that [Text.wordToNumber] would otherwise read as the number one. */
    private val NOT_NUMBERS = setOf("a", "an", "the", "of", "to", "at", "in", "on", "for")

    fun number(text: String): Entity? {
        for (token in Text.tokenize(text)) {
            if (token in NOT_NUMBERS) continue
            token.toDoubleOrNull()?.let { return Entity(EntityKind.NUMBER, token, token) }
            Text.wordToNumber(token)?.let { return Entity(EntityKind.NUMBER, it.toString(), token) }
        }
        NUMBER_REGEX.find(text)?.let {
            return Entity(EntityKind.NUMBER, it.groupValues[1], it.value)
        }
        return null
    }

    private val PERCENT_REGEX = Regex("\\b([0-9]+(?:\\.[0-9]+)?) ?(%|percent|percentage|pc)\\b")

    fun percent(text: String): Entity? {
        val match = PERCENT_REGEX.find(text) ?: return null
        return Entity(EntityKind.PERCENT, match.groupValues[1], match.value)
    }

    private val DIRECTION_WORDS = mapOf(
        "up" to "up", "down" to "down", "left" to "left", "right" to "right",
        "top" to "up", "bottom" to "down", "backwards" to "back", "backward" to "back",
        "forward" to "forward", "forwards" to "forward",
    )

    fun direction(text: String): Entity? {
        // Last directional word wins: "skip forward 30 seconds" and "scroll down" both read clean.
        val found = Text.tokenize(text).lastOrNull { DIRECTION_WORDS.containsKey(it) } ?: return null
        return Entity(EntityKind.DIRECTION, DIRECTION_WORDS.getValue(found), found)
    }

    /** Setting names, with every alias folded onto one canonical key. */
    private val SETTING_ALIASES: List<Pair<String, String>> = listOf(
        "do not disturb" to "dnd", "quiet mode" to "dnd", "quiet hours" to "dnd", "dnd" to "dnd",
        "flash light" to "torch", "flashlight" to "torch", "torch" to "torch",
        "wi-fi" to "wifi", "wi fi" to "wifi", "wifi" to "wifi", "wireless" to "wifi",
        "bluetooth" to "bluetooth",
        "airplane mode" to "airplane_mode", "flight mode" to "airplane_mode", "airplane" to "airplane_mode",
        "hotspot" to "hotspot", "tethering" to "hotspot",
        "mobile data" to "mobile_data", "cellular data" to "mobile_data",
        "battery saver" to "battery_saver", "power saving" to "battery_saver",
        "power saving mode" to "battery_saver", "battery saver mode" to "battery_saver",
        "auto rotate" to "rotation", "auto-rotate" to "rotation", "autorotate" to "rotation",
        "rotation" to "rotation", "screen rotation" to "rotation",
        "dark mode" to "dark_mode", "night mode" to "dark_mode", "light mode" to "dark_mode",
        "night light" to "night_light", "blue light filter" to "night_light",
        "screen timeout" to "screen_timeout", "sleep after" to "screen_timeout",
        "ringer mode" to "ringer_mode", "silent mode" to "ringer_mode", "vibrate only" to "ringer_mode",
        "ring mode" to "ringer_mode", "vibration mode" to "ringer_mode", "silent" to "ringer_mode",
        "location" to "location", "gps" to "location",
        "nfc" to "nfc",
        "volume" to "volume", "brightness" to "brightness",
        "lock screen" to "lock_device", "lock device" to "lock_device", "lock phone" to "lock_device",
    ).sortedByDescending { it.first.length }

    fun setting(text: String): Entity? {
        for ((alias, canonical) in SETTING_ALIASES) {
            if (Regex("\\b${Regex.escape(alias)}\\b").containsMatchIn(text)) {
                return Entity(EntityKind.SETTING, canonical, alias)
            }
        }
        return null
    }

    private val SETTING_VALUES = listOf(
        "turn off" to "off", "switch off" to "off", "turn on" to "on", "switch on" to "on",
        "enable" to "on", "disable" to "off", "activate" to "on", "deactivate" to "off",
        "toggle" to "toggle", "off" to "off", "on" to "on",
        "louder" to "up", "quieter" to "down", "brighter" to "up", "dimmer" to "down",
        "up" to "up", "down" to "down", "mute" to "off", "unmute" to "on",
    ).sortedByDescending { it.first.length }

    fun settingValue(text: String): Entity? {
        for ((alias, canonical) in SETTING_VALUES) {
            if (Regex("\\b${Regex.escape(alias)}\\b").containsMatchIn(text)) {
                return Entity(EntityKind.SETTING_VALUE, canonical, alias)
            }
        }
        return null
    }

    /** A requested level as 0-100, for volume and brightness. */
    fun level(text: String): Entity? {
        percent(text)?.let { return Entity(EntityKind.LEVEL, it.value, it.raw) }
        val match = Regex("\\b(?:to|at|set) ([0-9]{1,3})\\b").find(text) ?: return null
        val n = match.groupValues[1].toIntOrNull() ?: return null
        if (n !in 0..100) return null
        return Entity(EntityKind.LEVEL, n.toString(), match.groupValues[1])
    }

    /** The text the user wants typed, preferably from their own quoting. */
    fun spokenSpan(text: String, kind: EntityKind): Entity? {
        Text.firstQuotedSpan(text)?.let { return Entity(kind, it, it) }
        val match = Regex("\\b(?:saying|say|that says|reads|reading)\\b[: ]+(.{2,200})$").find(text)
            ?: return null
        val value = Text.stripQuotes(match.groupValues[1].trim().trimEnd('.', ',', '!'))
        return if (value.isBlank()) null else Entity(kind, value, value)
    }

    /** What to tap: the user's description of an on-screen element. */
    fun element(text: String): Entity? {
        Text.firstQuotedSpan(text)?.let { return Entity(EntityKind.ELEMENT_DESCRIPTION, it, it) }
        val match = Regex(
            "\\b(?:tap|click|press|select|choose|hit)\\b(?: on| into)?\\s+(?:the |that |this |a |an )?(.{1,60})$",
        ).find(text) ?: return null
        val value = cleanSlot(match.groupValues[1], stopWords = APP_STOP_WORDS)
        return if (value.isBlank()) null else Entity(EntityKind.ELEMENT_DESCRIPTION, value, value)
    }

    fun contact(text: String, leadIns: List<String>): Entity? {
        Text.firstQuotedSpan(text)?.let { return Entity(EntityKind.CONTACT, it, it) }
        val afterTo = Regex("\\bto ([a-z][a-z .'-]{1,40})$").find(text)
        if (afterTo != null) {
            val value = cleanSlot(afterTo.groupValues[1])
            if (value.isNotBlank() && !isNotAName(value)) {
                return Entity(EntityKind.CONTACT, value, value)
            }
        }
        val phrase = slotAfterLeadIn(text, leadIns) ?: return null
        val value = cleanSlot(phrase)
        if (value.isBlank() || isNotAName(value)) return null
        return Entity(EntityKind.CONTACT, value, value)
    }

    /** Recipient of a reminder ("remind mom to ..."), when one is named. */
    private val RECIPIENT_WORDS = setOf(
        "mom", "mum", "mother", "dad", "father", "brother", "sister", "wife", "husband",
        "son", "daughter", "boss", "team", "me", "us",
    )

    fun recipient(text: String): Entity? {
        val match = Regex("\\bremind ([a-z]+)\\b").find(text) ?: return null
        val who = match.groupValues[1]
        if (who !in RECIPIENT_WORDS || who == "me" || who == "us") return null
        return Entity(EntityKind.RECIPIENT, who, who)
    }

    private fun isNotAName(value: String): Boolean {
        val tokens = Text.tokenize(value)
        return tokens.isEmpty() ||
            tokens.all { it in TRAILING_NOISE || it in APP_STOP_WORDS } ||
            // "call it a day", "text back later", "phone screen": these are not people. Anything
            // on this list forces a clarifying question instead of a dialled guess.
            tokens.any { it in NOT_CONTACTS }
    }

    /** Words that can follow a messaging verb but are not a person. */
    private val NOT_CONTACTS = setOf(
        "it", "this", "that", "these", "those", "day", "night", "screen", "phone", "device",
        "app", "message", "messages", "text", "texts", "sms", "back", "later", "now", "someone",
        "anyone", "nothing", "everything", "number", "call", "off", "on", "up", "down",
    )

    fun topic(text: String, leadIns: List<String>, preferLast: Boolean = true): Entity? {
        val afterAbout = Regex("\\b(?:about|on|regarding|of)\\s+(.{1,80})$").find(text)
        if (afterAbout != null) {
            val value = cleanSlot(afterAbout.groupValues[1])
            if (value.isNotBlank()) return Entity(EntityKind.TOPIC, value, value)
        }
        val phrase = slotAfterLeadIn(text, leadIns, preferLast) ?: return null
        val value = cleanSlot(phrase)
        return if (value.isBlank()) null else Entity(EntityKind.TOPIC, value, value)
    }

    fun query(text: String, leadIns: List<String>, preferLast: Boolean = true): Entity? {
        Text.firstQuotedSpan(text)?.let { return Entity(EntityKind.QUERY, it, it) }
        val phrase = slotAfterLeadIn(text, leadIns, preferLast) ?: return null
        val value = cleanSlot(phrase).trimEnd('?', '.', '!', ',', ':')
        if (value.isBlank() || value.length < 2) return null
        return Entity(EntityKind.QUERY, value, value)
    }

    private val PREFERENCE_KEYS = listOf(
        "dark mode" to "theme", "light mode" to "theme", "theme" to "theme",
        "language" to "language", "voice" to "voice",
        "wake word" to "wake_word", "notification" to "notifications", "notifications" to "notifications",
        "units" to "units", "temperature" to "temperature_units", "currency" to "currency",
        "volume" to "volume", "brightness" to "brightness", "font" to "font", "font size" to "font_size",
        "timezone" to "timezone", "time zone" to "timezone", "name" to "name", "email" to "email",
        "birthday" to "birthday", "diet" to "diet", "food" to "food", "coffee" to "coffee",
        "work hours" to "work_hours", "sleep" to "sleep", "music" to "music", "news" to "news",
    ).sortedByDescending { it.first.length }

    /** Alias -> canonical key, for lookup once the longest matching alias is known. */
    private val PREFERENCE_CANONICAL: Map<String, String> = PREFERENCE_KEYS.toMap()

    /** Which preference the user is talking about, e.g. "dark mode" -> `theme`. */
    fun preferenceKey(text: String): Entity? {
        val alias = firstPreferenceAlias(text) ?: return null
        val canonical = PREFERENCE_CANONICAL[alias] ?: return null
        return Entity(EntityKind.PREFERENCE_KEY, canonical, alias)
    }

    /**
     * Aliases that already contain the value: "dark mode" says both the setting (theme) and what
     * to set it to (dark), so there is nothing left for the user to be asked.
     */
    private val ALIAS_CARRIES_VALUE = mapOf(
        "dark mode" to "dark", "light mode" to "light", "night mode" to "dark",
    )

    /** What they want it set to, when they said. Absent means the caller must ask. */
    fun preferenceValueEntity(text: String): Entity? {
        val alias = firstPreferenceAlias(text) ?: return null
        val value = ALIAS_CARRIES_VALUE[alias] ?: preferenceValue(text, alias) ?: return null
        return Entity(EntityKind.PREFERENCE_VALUE, value, value)
    }

    private fun firstPreferenceAlias(text: String): String? {
        // Longest alias first, so "font size" is not read as "font".
        return PREFERENCE_KEYS.firstOrNull { (alias, _) ->
            Regex("\\b${Regex.escape(alias)}\\b").containsMatchIn(text)
        }?.first
    }

    private fun preferenceValue(text: String, alias: String): String? {
        val after = Regex("\\b${Regex.escape(alias)}\\b\\s+(?:to be |to |as |is )?([a-z0-9 ._-]{1,30})")
            .find(text)?.groupValues?.get(1)?.trim()
        return after?.takeIf { it.isNotBlank() && Text.tokenize(it).any { t -> t !in TRAILING_NOISE } }
            ?.let { cleanSlot(it) }
            ?.takeIf { it.isNotBlank() }
    }

    private val NAME_LEAD_INS = listOf(
        "call you", "your name is", "you're", "you are", "rename yourself", "rename you",
        "change your name to", "change your name", "new name", "from now on your name is",
        "i'll call you", "i will call you", "name is", "to",
    ).sortedByDescending { it.length }

    /** The new name the user wants to give the assistant. */
    fun assistantName(text: String, config: AssistantConfig): Entity? {
        Text.firstQuotedSpan(text)?.let { quoted ->
            return validateName(quoted)?.let { Entity(EntityKind.ASSISTANT_NAME, it, quoted) }
        }
        for (leadIn in NAME_LEAD_INS) {
            val index = text.indexOf(leadIn)
            if (index < 0) continue
            val rest = text.substring(index + leadIn.length).trim()
            val value = cleanSlot(rest, stopWords = NAME_STOP_WORDS)
            val validated = validateName(value) ?: continue
            // "what should i call you?" asks for the current name; it does not set a new one.
            if (text.trimEnd().endsWith("?") && validated.equals(config.nickname, ignoreCase = true)) {
                return null
            }
            return Entity(EntityKind.ASSISTANT_NAME, validated, validated)
        }
        return null
    }

    private val NAME_STOP_WORDS = setOf(
        "please", "from", "now", "on", "your", "my", "the", "name", "assistant", "ok", "okay",
        "i", "ll", "will", "call", "you", "to", "be", "and", "thanks",
    )

    /**
     * Words that mean the extraction failed rather than forming part of a name.
     *
     * Narrower than [NAME_STOP_WORDS] on purpose: articles are stripped from the edges of a
     * candidate but are legal inside one, so "The Boss" is a name while "to be" is not.
     */
    private val NAME_REJECT_TOKENS = setOf(
        "to", "be", "your", "my", "name", "call", "you", "please", "from", "now", "on",
        "assistant", "rename", "change", "is", "are", "what", "should", "i", "ll", "will",
        "and", "thanks", "then",
    )

    private fun validateName(candidate: String): String? {
        val trimmed = candidate.trim().trim('.', ',', '!', '?')
        if (trimmed.isEmpty() || trimmed.length > AssistantConfig.MAX_NICKNAME_LENGTH) return null
        // A name is not a sentence: reject anything that still carries lead-in words.
        val tokens = trimmed.split(" ").filter { it.isNotBlank() }
        if (tokens.size > 4) return null
        if (tokens.any { it in NAME_REJECT_TOKENS }) return null
        return trimmed
    }

    private val DOCUMENT_REGEX = Regex("([^\\s]+\\.(pdf|txt|md|docx?|epub|csv|json|rtf|pages))")

    fun document(text: String): Entity? {
        DOCUMENT_REGEX.find(text)?.let { return Entity(EntityKind.DOCUMENT, it.groupValues[1], it.value) }
        Text.firstQuotedSpan(text)?.let { return Entity(EntityKind.DOCUMENT, it, it) }
        val match = Regex("\\b(?:called|named|titled)\\s+(.{1,60})$").find(text)
        if (match != null) {
            val value = cleanSlot(match.groupValues[1])
            if (value.isNotBlank()) return Entity(EntityKind.DOCUMENT, value, value)
        }
        return null
    }

    /** "when I plug in the charger" -> the trigger clause. */
    fun automationTrigger(text: String): Entity? {
        val match = Regex(
            "\\b(whenever|every time|each time|any time|when|if|as soon as)\\s+(.{2,90}?)" +
                "(?:\\bthen\\b|\\bplease\\b|\\bautomatically\\b|$)",
        ).find(text) ?: return null
        val value = match.groupValues[2].trim().trimEnd(',', '.')
        if (value.isBlank()) return null
        return Entity(EntityKind.AUTOMATION_TRIGGER, value, match.value.trim())
    }

    /** The action clause of an automation, i.e. everything after the trigger. */
    fun automationAction(text: String): Entity? {
        val separators = listOf(" then ", " please ", " automatically ", ", ")
        for (separator in separators) {
            val index = text.indexOf(separator)
            if (index < 0) continue
            val value = text.substring(index + separator.length).trim().trimEnd(',', '.')
            if (value.length >= 3) return Entity(EntityKind.AUTOMATION_ACTION, value, value)
        }
        // No separator: the action is whatever follows the trigger clause.
        val trigger = automationTrigger(text) ?: return null
        val afterTrigger = text.substringAfter(trigger.raw, "")
        val value = afterTrigger.trim().trimStart(',', ' ').trimEnd(',', '.')
        return if (value.length >= 3) Entity(EntityKind.AUTOMATION_ACTION, value, value) else null
    }

    private val EXPRESSION_REGEX = Regex("([0-9][0-9 .]*\\s*(?:[+\\-*/x×÷]|plus|minus|times|multiplied by|divided by|percent of|%of|% of)\\s*[0-9][0-9 .%]*)")

    /** The arithmetic the user asked for, kept verbatim for the evaluator. */
    fun expression(text: String): Entity? {
        EXPRESSION_REGEX.find(text)?.let {
            return Entity(EntityKind.QUERY, it.value.trim(), it.value.trim())
        }
        val match = Regex("\\b([0-9]+(?:\\.[0-9]+)?) ?(?:percent|%|pc) of ([0-9]+(?:\\.[0-9]+)?)\\b")
            .find(text)
        if (match != null) {
            val value = "${match.groupValues[1]} percent of ${match.groupValues[2]}"
            return Entity(EntityKind.QUERY, value, value)
        }
        return null
    }

    private val CONVERT_REGEX = Regex(
        "\\b([0-9]+(?:\\.[0-9]+)?)\\s*([a-z°%]{1,20})?\\s*(?:to|in|into|is)\\s*([a-z°%]{1,20})\\b",
    )

    /** "100 usd to inr" -> source amount, source unit, target unit. */
    fun conversion(text: String): Entity? {
        val match = CONVERT_REGEX.find(text) ?: return null
        val amount = match.groupValues[1]
        val from = match.groupValues[2].trim()
        val to = match.groupValues[3].trim()
        if (to.isBlank()) return null
        val value = listOf(amount, from, "to", to).filter { it.isNotBlank() }.joinToString(" ")
        return Entity(EntityKind.QUERY, value, match.value.trim())
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    /**
     * Find a lead-in phrase in [text] and return everything after it.
     *
     * By default the *latest* lead-in wins, so "remind me to" beats "remind me" beats "to" -
     * otherwise "remind me to call mom" would yield "me to call mom".
     *
     * [preferLast] is false for "verb ... destination" phrasings such as "add buy milk to my
     * to-do list", where the content sits between the first verb and the trailing destination.
     * Taking the latest lead-in there would return the destination itself.
     */
    private fun slotAfterLeadIn(
        text: String,
        leadIns: List<String>,
        preferLast: Boolean = true,
    ): String? {
        var best: String? = null
        var bestEnd = if (preferLast) -1 else Int.MAX_VALUE
        for (leadIn in leadIns.sortedByDescending { it.length }) {
            val pattern = if (leadIn.startsWith("^")) leadIn else "\\b${Regex.escape(leadIn)}\\b"
            val match = Regex(pattern).find(text) ?: continue
            val better = if (preferLast) match.range.last > bestEnd else match.range.last < bestEnd
            if (better) {
                bestEnd = match.range.last
                best = text.substring(match.range.last + 1).trim()
            }
        }
        return best?.takeIf { it.isNotBlank() }
    }

    /** Strip leading articles and trailing noise, and cut at a clause boundary. */
    private fun cleanSlot(phrase: String, stopWords: Set<String> = emptySet()): String {
        val cut = phraseUntil(phrase, stopWords) ?: return ""
        var tokens = Text.tokenize(cut).filter { it.isNotBlank() }
        while (tokens.isNotEmpty() &&
            (tokens.first() in LEADING_ARTICLES || tokens.first() in LEADING_CONNECTORS ||
                tokens.first() in stopWords)
        ) {
            tokens = tokens.drop(1)
        }
        while (tokens.isNotEmpty() && (tokens.last() in TRAILING_NOISE || tokens.last() in stopWords)) {
            tokens = tokens.dropLast(1)
        }
        val value = tokens.joinToString(" ").trim().trimEnd('.', ',', '!', '?', ':', ';')
        return stripDestination(value)
    }

    private val LEADING_ARTICLES = setOf("the", "a", "an", "my", "your", "this", "that", "some", "any")

    /** Connectors that introduce a clause but are not part of its content. */
    private val LEADING_CONNECTORS = setOf("that", "about", "to", "of", "it")

    /**
     * Phrases that say *where* something goes rather than *what* it is.
     *
     * "add buy milk to my to-do list" has the content "buy milk"; leaving the destination in the
     * task title would store a task called "buy milk to my to do list" and read it back verbatim.
     * Only suffixes are stripped, so a task genuinely about a list is left alone.
     */
    private val TRAILING_DESTINATIONS = listOf(
        "to my to do list", "to my todo list", "to my task list", "to my tasks", "to my list",
        "on my list", "in my list", "to do list", "todo list",
        "into your knowledge base", "into your knowledge", "to your knowledge base",
        "in my calendar", "to my calendar", "on my calendar",
        "as done", "as complete", "as completed", "as finished", "done", "off",
        "in chrome", "in youtube", "in maps", "in settings", "in the app",
    ).sortedByDescending { it.length }

    private fun stripDestination(value: String): String {
        var result = value.trim()
        var changed = true
        while (changed) {
            changed = false
            for (destination in TRAILING_DESTINATIONS) {
                if (result.endsWith(destination) && result.length > destination.length) {
                    result = result.removeSuffix(destination).trim().trimEnd(',', '.')
                    changed = true
                }
            }
        }
        return result.trim()
    }

    /** Take tokens until one of [stopWords] or a clause separator appears. */
    private fun phraseUntil(text: String, stopWords: Set<String>): String? {
        val tokens = Text.tokenize(text)
        if (tokens.isEmpty()) return null
        val taken = ArrayList<String>(tokens.size)
        for (token in tokens) {
            if (token in stopWords) break
            taken += token
        }
        return if (taken.isEmpty()) null else taken.joinToString(" ")
    }
}
