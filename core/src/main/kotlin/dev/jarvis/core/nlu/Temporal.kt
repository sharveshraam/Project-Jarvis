package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.model.Recurrence
import dev.jarvis.core.util.ClockPort
import dev.jarvis.core.util.Text
import kotlin.math.abs
import kotlin.math.roundToLong

/**
 * Natural-language time.
 *
 * Requirement §8 is that "remind me to finish my electronics assignment after dinner" must
 * work without a predefined command syntax, so time understanding is a real parser rather than
 * a set of regexes over a fixed grammar.
 *
 * It handles: relative offsets ("in 20 minutes", "in half an hour"), clock times ("at 7pm",
 * "at 19:30", "7:30 in the morning"), day references ("today", "tomorrow", "tonight",
 * "on Friday", "next Monday"), named parts of the user's own day ("after dinner",
 * "in the morning" - resolved through [AssistantConfig.daypartHours], because dinner is at 8pm
 * for some people and 10pm for others), durations ("for 5 minutes", "after five minutes") and
 * recurrence ("every Sunday at 7pm", "daily at 6", "every 30 minutes").
 *
 * Anything it cannot parse becomes [TimeReference.Unresolved] rather than a guess. A reminder
 * set for the wrong time is worse than the assistant asking.
 */
sealed class TimeReference {
    /** The user's own words. */
    abstract val phrase: String

    /** How to say it back. */
    abstract fun describe(): String

    /**
     * A specific local time, [dayOffset] days from today.
     *
     * Carrying a day offset rather than an epoch instant keeps this testable: resolution
     * against "now" happens in [TemporalParser.resolve], not here.
     */
    data class At(
        override val phrase: String,
        val dayOffset: Int,
        val hour: Int,
        val minute: Int,
        /** Named day, when the user said one ("friday"). Used for the spoken description. */
        val dayName: String? = null,
    ) : TimeReference() {
        override fun describe(): String {
            val day = when (dayOffset) {
                0 -> "today"
                1 -> "tomorrow"
                2 -> "the day after tomorrow"
                else -> dayName?.let { "on $it" } ?: "in $dayOffset days"
            }
            return "$day at ${formatClock(hour, minute)}"
        }
    }

    /** A delay from now. */
    data class In(override val phrase: String, val offsetMillis: Long) : TimeReference() {
        override fun describe(): String = "in ${describeDuration(offsetMillis)}"
    }

    /** A named part of the day, from the user's own configured schedule. */
    data class Daypart(
        override val phrase: String,
        val daypart: String,
        val offsetMillis: Long,
        val before: Boolean,
    ) : TimeReference() {
        override fun describe(): String =
            (if (before) "before " else "after ") + daypart +
                if (offsetMillis == 0L) "" else " by ${describeDuration(offsetMillis)}"
    }

    /** A repeating time. */
    data class RecurringAt(
        override val phrase: String,
        val recurrence: Recurrence,
    ) : TimeReference() {
        override fun describe(): String = describeRecurrence(recurrence)
    }

    /** Could not be parsed. Never silently treated as "now". */
    data class Unresolved(override val phrase: String) : TimeReference() {
        override fun describe(): String = "at an unclear time ('$phrase')"
    }
}

class TemporalParser(
    private val clock: ClockPort,
    private val config: () -> AssistantConfig = { AssistantConfig.DEFAULT },
) {

    companion object {
        const val MINUTE_MILLIS = 60_000L
        const val HOUR_MILLIS = 3_600_000L
        const val DAY_MILLIS = 86_400_000L

        /** Monday-first, matching ClockPort.localDayOfWeek. */
        val WEEKDAYS = listOf(
            "monday" to 0, "tuesday" to 1, "wednesday" to 2, "thursday" to 3,
            "friday" to 4, "saturday" to 5, "sunday" to 6,
        )
        private val WEEKDAY_ALIASES = mapOf(
            "mon" to 0, "tue" to 1, "tues" to 1, "wed" to 2, "thu" to 3, "thur" to 3,
            "thurs" to 3, "fri" to 4, "sat" to 5, "sun" to 6,
            "weekday" to -1, "weekend" to -2,
        )

        private val DURATION_UNITS = mapOf(
            "second" to 1_000L, "sec" to 1_000L, "secs" to 1_000L, "seconds" to 1_000L,
            "minute" to MINUTE_MILLIS, "min" to MINUTE_MILLIS, "mins" to MINUTE_MILLIS,
            "minutes" to MINUTE_MILLIS,
            "hour" to HOUR_MILLIS, "hr" to HOUR_MILLIS, "hrs" to HOUR_MILLIS, "hours" to HOUR_MILLIS,
            "day" to DAY_MILLIS, "days" to DAY_MILLIS,
            "week" to 7 * DAY_MILLIS, "weeks" to 7 * DAY_MILLIS,
        )

        private val DAYPART_WORDS = listOf(
            "early morning", "morning", "breakfast", "noon", "lunch", "afternoon",
            "evening", "dinner", "night", "bedtime", "supper", "tea",
        )

        /** Phrases that mean "the coming night", resolved as 21:00 unless configured. */
        private const val TONIGHT_HOUR = 21
    }

    // ------------------------------------------------------------------- day maths ----

    /** Epoch millis of local midnight for [nowMillis]. */
    fun localDayStart(nowMillis: Long = clock.nowMillis()): Long {
        val offsetMillis = clock.zoneOffsetSeconds() * 1000L
        val shifted = nowMillis + offsetMillis
        val dayIndex = Math.floorDiv(shifted, DAY_MILLIS)
        return dayIndex * DAY_MILLIS - offsetMillis
    }

    /**
     * Whole local days between [nowMillis] and [epochMillis].
     *
     * Both instants are floored to their own local midnight first. Comparing raw instants would
     * make 23:00 today read as "1 day from now" at 09:30, which would put reminders on the wrong
     * day and make [describe] say "tomorrow" for something happening this evening.
     */
    fun dayOffsetOf(epochMillis: Long, nowMillis: Long = clock.nowMillis()): Int {
        val days = Math.round(
            (localDayStart(epochMillis) - localDayStart(nowMillis)).toDouble() / DAY_MILLIS,
        )
        return days.toInt()
    }

    // -------------------------------------------------------------------- durations ----

    /**
     * Parses a duration phrase to millis: "5 minutes", "half an hour", "twenty seconds",
     * "1.5 hours", "an hour and a half".
     */
    fun parseDuration(phrase: String): Long? {
        val text = Text.normalize(phrase).trim()
        if (text.isEmpty()) return null

        // "an hour and a half", "two hours and fifteen minutes"
        if (text.contains(" and ")) {
            val parts = text.split(" and ")
            val totals = parts.mapNotNull { parseSingleDuration(it) }
            if (totals.size == parts.size && totals.isNotEmpty()) return totals.sum()
        }

        return parseSingleDuration(text)
    }

    /**
     * One duration expression, in the forms people actually say.
     *
     * Order matters. Fractions are checked before plain amounts so "in half an hour" is 30 minutes
     * rather than the "an hour" hidden inside it, and a normalised decimal is reconstructed before
     * the number/unit walk so "1.5 hours" - which [Text.normalize] turns into "1 5 hours" - is not
     * read as 5 hours.
     */
    private fun parseSingleDuration(text: String): Long? {
        val trimmed = text.trim().removePrefix("for ").removePrefix("after ").removePrefix("in ")
            .removeSuffix(" long").trim()
        if (trimmed.isEmpty()) return null

        // "half an hour", "a quarter of an hour"
        val withoutArticle = trimmed.removePrefix("a ").removePrefix("an ").trim()
        val fraction = when {
            withoutArticle.startsWith("half ") -> 0.5
            withoutArticle.startsWith("quarter ") -> 0.25
            else -> null
        }
        if (fraction != null) {
            val remainder = withoutArticle.substringAfter(' ').removePrefix("of ").trim()
            val unit = parseSingleDuration("1 $remainder")
            if (unit != null) return (unit * fraction).roundToLong()
        }

        val tokens = Text.tokenize(trimmed)
        if (tokens.isEmpty()) return null

        // A lone number with a unit attached: "5m", "10s", "2h"
        val compact = Regex("^(\\d+(?:\\.\\d+)?)\\s*([smhd])$").find(trimmed.replace(" ", ""))
        if (compact != null) {
            val amount = compact.groupValues[1].toDoubleOrNull()
            val unit = when (compact.groupValues[2]) {
                "s" -> 1_000L
                "m" -> MINUTE_MILLIS
                "h" -> HOUR_MILLIS
                else -> DAY_MILLIS
            }
            if (amount != null) return (amount * unit).roundToLong()
        }

        // A decimal whose point normalise() folded into a space: "1 5 hours" means 1.5 hours.
        // Only a single trailing digit is read this way, so "5 30 minutes" is left to the walk below.
        if (tokens.size == 3 && tokens[0].toDoubleOrNull() != null &&
            tokens[1].length == 1 && tokens[1][0].isDigit() &&
            DURATION_UNITS.containsKey(tokens[2])
        ) {
            val amount = "${tokens[0]}.${tokens[1]}".toDoubleOrNull()
            val unit = DURATION_UNITS[tokens[2]]
            if (amount != null && unit != null) return (amount * unit).roundToLong()
        }

        // Sum every number+unit pair: "5 minutes", "1 hour 30 minutes", "2 days 3 hours".
        var total = 0L
        var matched = false
        var index = 0
        while (index < tokens.size - 1) {
            val amount = tokens[index].toDoubleOrNull() ?: Text.wordToNumber(tokens[index])?.toDouble()
            val unit = DURATION_UNITS[tokens[index + 1]]
            if (amount != null && unit != null) {
                total += (amount * unit).roundToLong()
                matched = true
                index += 2
            } else {
                index++
            }
        }
        if (matched) return total

        // "an hour", "a minute", "one day"
        val unitToken = tokens.firstOrNull { DURATION_UNITS.containsKey(it) }
        if (unitToken != null && tokens.any { it == "a" || it == "an" || it == "one" }) {
            return DURATION_UNITS[unitToken]
        }
        return null
    }

    // ------------------------------------------------------------------ clock times ----

    /** A time-of-day with no date attached. */
    data class ClockTime(val hour: Int, val minute: Int)

    /**
     * Finds a time of day inside [text]: "7 pm", "7pm", "19:30", "half past seven",
     * "quarter to eight", "7:30 in the morning", "noon", "midnight".
     */
    fun parseClockTime(text: String): ClockTime? {
        val normalized = Text.normalize(text).replace(" :", ":").trim()
        if (normalized.isEmpty()) return null

        if (normalized.contains("noon") || normalized.contains("midday")) return ClockTime(12, 0)
        if (normalized.contains("midnight")) return ClockTime(0, 0)

        // "half past seven", "quarter past nine", "quarter to eight"
        val spoken = Regex("(half|quarter)\\s+(past|after|to|before)\\s+([a-z]+|\\d+)").find(normalized)
        if (spoken != null) {
            val fraction = spoken.groupValues[1]
            val direction = spoken.groupValues[2]
            val base = Text.wordToNumber(spoken.groupValues[3]) ?: spoken.groupValues[3].toIntOrNull()
            if (base != null) {
                val baseHour = to24Hour(base, normalized)
                val minute = if (fraction == "half") 30 else 15
                return if (direction == "to" || direction == "before") {
                    val adjusted = if (baseHour == 0) 23 else baseHour - 1
                    ClockTime(adjusted, 60 - minute)
                } else {
                    ClockTime(baseHour, minute)
                }
            }
        }

        // Explicit "h:mm" with an optional am/pm or daypart qualifier.
        val withMinutes = Regex("(\\d{1,2}):(\\d{2})\\s*(am|pm|a m|p m)?").find(normalized)
        if (withMinutes != null) {
            val rawHour = withMinutes.groupValues[1].toIntOrNull() ?: return null
            val minute = withMinutes.groupValues[2].toIntOrNull() ?: return null
            if (minute > 59) return null
            val meridiem = withMinutes.groupValues[3].replace(" ", "")
            return ClockTime(applyMeridiem(rawHour, meridiem.ifEmpty { null }, normalized), minute)
        }

        // "14 30" - Text.normalize folds ':' into a space, so text that has already been
        // normalised never matches the colon form above. Two adjacent numbers where the second is
        // exactly two digits and a valid minute read back as h:mm. The leading non-digit boundary
        // keeps this from firing inside a longer number such as a phone number.
        val spaced = Regex("(?:^|[^\\d:])(\\d{1,2}) (\\d{2})(?:\\s*(am|pm))?(?:[^\\d]|$)")
            .find(normalized)
        if (spaced != null) {
            val rawHour = spaced.groupValues[1].toIntOrNull()
            val minute = spaced.groupValues[2].toIntOrNull()
            if (rawHour != null && minute != null && rawHour in 0..23 && minute <= 59) {
                val meridiem = spaced.groupValues[3].replace(" ", "")
                return ClockTime(applyMeridiem(rawHour, meridiem.ifEmpty { null }, normalized), minute)
            }
        }

        // "7 pm", "7pm", "19", "seven in the evening"
        val bareHour = Regex("(?:^|[^\\d:])(\\d{1,2})\\s*(am|pm)?(?:[^\\d:]|$)").find(normalized)
        if (bareHour != null) {
            val rawHour = bareHour.groupValues[1].toIntOrNull() ?: return null
            val meridiem = bareHour.groupValues[2]
            if (rawHour in 0..23) {
                return ClockTime(applyMeridiem(rawHour, meridiem.ifEmpty { null }, normalized), 0)
            }
        }

        val wordHour = Regex("(?:^|\\s)([a-z]+)\\s+(?:in\\s+the\\s+)?(morning|afternoon|evening|night)(?:\\s|$)")
            .find(normalized)
        if (wordHour != null) {
            val hour = Text.wordToNumber(wordHour.groupValues[1]) ?: return null
            return ClockTime(applyMeridiem(hour, null, normalized), 0)
        }
        return null
    }

    /**
     * Applies am/pm, or a daypart when no meridiem was spoken.
     *
     * "remind me at 7" is ambiguous: 7am has usually already happened by the time someone says
     * it in the evening, so an unqualified hour is pushed to whichever occurrence is next
     * rather than assumed to be the morning.
     */
    private fun applyMeridiem(rawHour: Int, meridiem: String?, context: String): Int {
        if (rawHour > 23) return rawHour
        val text = context.lowercase()
        return when {
            meridiem == "am" -> if (rawHour == 12) 0 else rawHour
            meridiem == "pm" -> if (rawHour == 12) 12 else rawHour + 12
            text.contains("in the morning") || text.contains("this morning") ->
                if (rawHour == 12) 0 else rawHour
            text.contains("in the afternoon") -> if (rawHour < 12) rawHour + 12 else rawHour
            text.contains("in the evening") || text.contains("at night") ||
                text.contains("tonight") || text.contains("this evening") ->
                if (rawHour < 12) rawHour + 12 else rawHour
            // Unqualified: pick the next plausible occurrence.
            rawHour in 1..7 -> {
                val now = clock.localHour()
                if (now < rawHour) rawHour else rawHour + 12
            }
            else -> rawHour
        }.coerceIn(0, 23)
    }

    private fun to24Hour(hour: Int, context: String): Int = applyMeridiem(hour, null, context)

    // --------------------------------------------------------------------- recurrence ----

    /**
     * True when a weekday is named in the plural: "on mondays at 9", "every weekday at 8".
     *
     * The plural is what distinguishes a repeating rule from a one-off appointment.
     */
    private fun containsPluralWeekday(text: String): Boolean {
        if (containsWord(text, "weekdays") || containsWord(text, "weekends")) return true
        val names = WEEKDAYS.map { it.first } + WEEKDAY_ALIASES.keys
        return names.any { name ->
            Regex("(?:^|[^a-z])${Regex.escape(name)}s(?:[^a-z]|$)").containsMatchIn(text)
        }
    }

    /** Parses "every Sunday at 7pm", "daily at 6", "every 30 minutes", "on mondays at 8". */
    fun parseRecurrence(phrase: String): Recurrence? {
        val text = Text.normalize(phrase)
        // "on friday at 6pm" means the coming Friday, not every Friday; only a *plural* weekday
        // ("on fridays") asks for a weekly repeat. Treating the singular as recurring would set up
        // a permanent alarm from a one-off request.
        if (!text.startsWith("every") && !text.startsWith("each") &&
            !text.contains(" daily") && !text.contains(" weekly") && !text.startsWith("daily") &&
            !text.startsWith("weekly") && !containsPluralWeekday(text)
        ) {
            return null
        }

        val time = parseClockTime(text)
        val body = text.removePrefix("every").removePrefix("each").trim()

        // "every 30 minutes" / "every hour"
        val intervalDuration = parseSingleDuration(body.substringBefore(" at ").trim())
        val intervalUnit = body.substringBefore(" at ").trim().let { Text.tokenize(it).lastOrNull() }
        if (intervalDuration != null && intervalUnit != null &&
            DURATION_UNITS.containsKey(intervalUnit) && time == null
        ) {
            val frequency = when (intervalUnit) {
                "second", "seconds", "sec", "secs" -> Recurrence.Frequency.MINUTELY
                "minute", "minutes", "min", "mins" -> Recurrence.Frequency.MINUTELY
                "hour", "hours", "hr", "hrs" -> Recurrence.Frequency.HOURLY
                "day", "days" -> Recurrence.Frequency.DAILY
                "week", "weeks" -> Recurrence.Frequency.WEEKLY
                else -> null
            }
            if (frequency != null) {
                val everyN = Text.tokenize(body.substringBefore(" at "))
                    .firstOrNull()?.let { it.toIntOrNull() ?: Text.wordToNumber(it) } ?: 1
                val unitMillis = DURATION_UNITS[intervalUnit] ?: MINUTE_MILLIS
                // Preserve the interval: "every 30 minutes" must not become "every minute".
                val interval = unitMillis * everyN.coerceAtLeast(1)
                return Recurrence(
                    frequency = frequency,
                    intervalMillis = when (frequency) {
                        Recurrence.Frequency.MINUTELY, Recurrence.Frequency.HOURLY -> interval
                        else -> null
                    },
                )
            }
        }

        if (body.startsWith("day") || body.startsWith("days") || text.contains("daily") ||
            body.startsWith("morning") || body.startsWith("evening") || body.startsWith("night")
        ) {
            return Recurrence(
                frequency = Recurrence.Frequency.DAILY,
                hourOfDay = time?.hour,
                minute = time?.minute ?: 0,
            )
        }
        if (body.startsWith("week") || text.contains("weekly")) {
            return Recurrence(
                frequency = Recurrence.Frequency.WEEKLY,
                hourOfDay = time?.hour,
                minute = time?.minute ?: 0,
            )
        }

        // Explicit weekday list: "monday and friday", "mondays", "weekdays", "weekend".
        // A trailing "s" is accepted so the plural form that got us here is recognised.
        val days = mutableSetOf<Int>()
        for ((name, index) in WEEKDAYS) {
            if (Regex("(?:^|[^a-z])${name}s?(?:[^a-z]|$)").containsMatchIn(body)) {
                days += index
            }
        }
        for ((alias, index) in WEEKDAY_ALIASES) {
            if (index < 0) continue
            if (Regex("(?:^|[^a-z])${alias}s?(?:[^a-z]|$)").containsMatchIn(body)) days += index
        }
        if (body.contains("weekday")) days += (0..4)
        if (body.contains("weekend")) days += setOf(5, 6)

        if (days.isNotEmpty()) {
            return Recurrence(
                frequency = Recurrence.Frequency.WEEKLY,
                daysOfWeek = days,
                hourOfDay = time?.hour,
                minute = time?.minute ?: 0,
            )
        }
        return null
    }

    // ------------------------------------------------------------------------ parsing ----

    /**
     * Parses a time expression out of a longer sentence.
     *
     * Returns null when the sentence contains no time expression at all, and
     * [TimeReference.Unresolved] when it clearly contains one that could not be understood -
     * the two cases need different responses ("when?" versus "I did not catch the time").
     */
    fun parse(phrase: String): TimeReference? {
        val text = Text.normalize(phrase)
        if (text.isEmpty()) return null

        parseRecurrence(text)?.let { return TimeReference.RecurringAt(phrase.trim(), it) }

        // "in 5 minutes", "after 10 minutes", "in half an hour"
        val relative = Regex("(?:^|\\s)(?:in|after)\\s+(.+?)(?:\\s*$|\\s+(?:remind|tell|notify|ping|ask)\\b)")
            .find(text)
        if (relative != null) {
            val duration = parseDuration(relative.groupValues[1])
            if (duration != null) return TimeReference.In(phrase.trim(), duration)
        }
        val bareRelative = Regex("(?:^|\\s)(\\d+\\s*(?:minutes?|mins?|hours?|hrs?|seconds?|secs?|days?))\\s*(?:from now|later)?\\s*$")
            .find(text)
        if (bareRelative != null) {
            parseDuration(bareRelative.groupValues[1])?.let {
                return TimeReference.In(phrase.trim(), it)
            }
        }

        val dayOffset = parseDayOffset(text)
        val dayName = parseDayName(text)
        val time = parseClockTime(text)
        val daypart = parseDaypart(text)

        // An explicit clock time wins. "half past seven in the evening" is 19:30, and the daypart
        // has already been used to disambiguate the hour inside parseClockTime, so returning the
        // daypart here would throw away the time the user actually said.
        if (time != null) {
            val resolvedDay = when {
                dayName != null -> weekdayOffset(dayName, text, time)
                dayOffset != null -> dayOffset
                else -> 0
            }
            return TimeReference.At(
                phrase = phrase.trim(),
                dayOffset = resolvedDay,
                hour = time.hour,
                minute = time.minute,
                dayName = dayName,
            )
        }

        // A named day plus a daypart but no clock time: "tomorrow evening" means tomorrow at the
        // user's configured evening hour. A Daypart cannot carry a day offset, so it must not be
        // returned here or "tomorrow evening" would resolve to today.
        if (daypart != null && (dayOffset != null || dayName != null)) {
            val hour = config().daypartHour(daypart.daypart)
                ?: config().daypartHour(daypart.daypart.removePrefix("early "))
            if (hour != null) {
                val resolvedDay = dayName?.let { weekdayOffset(it, text, null) } ?: dayOffset ?: 0
                return TimeReference.At(phrase.trim(), resolvedDay, hour, 0, dayName)
            }
        }

        if (daypart != null) return daypart

        if (dayOffset != null || dayName != null) {
            val resolvedDay = dayName?.let { weekdayOffset(it, text, null) } ?: dayOffset ?: 0
            // A day with no time of day: default to the user's configured morning, and say so.
            val hour = config().daypartHour("morning") ?: 9
            return TimeReference.At(phrase.trim(), resolvedDay, hour, 0, dayName)
        }

        return if (containsTimeHint(text)) TimeReference.Unresolved(phrase.trim()) else null
    }

    /**
     * Does this text look like it was *trying* to name a time?
     *
     * Word boundaries matter: a substring test for "at " matches "wh**at** can you do", which
     * would make an ordinary question come back as an unresolved time and prompt the user for a
     * time they never mentioned.
     */
    private fun containsTimeHint(text: String): Boolean = TIME_HINT.containsMatchIn(text)

    private val TIME_HINT = Regex(
        "(?:^|[^a-z])(at|every|each|tomorrow|today|tonight|yesterday|later|soon|daily|weekly" +
            "|morning|evening|afternoon|night|noon|midnight|am|pm)(?:[^a-z]|$)",
    )

    /**
     * "after dinner", "before lunch", "in the morning", "tonight".
     *
     * Daypart words are matched on word boundaries. A plain substring search would find "noon"
     * inside "afternoon" and "night" inside "tonight", silently scheduling things three hours
     * early.
     */
    private fun parseDaypart(text: String): TimeReference.Daypart? {
        val cfg = config()

        // "tonight" is its own daypart so it can be spoken back as "tonight".
        if (containsWord(text, "tonight") || text.contains("this night")) {
            return TimeReference.Daypart(text.trim(), "tonight", 0L, before = false)
        }

        for (word in DAYPART_WORDS) {
            val match = Regex("(?:^|[^a-z])${Regex.escape(word)}(?:[^a-z]|$)").find(text) ?: continue
            val hour = cfg.daypartHour(word) ?: cfg.daypartHour(word.removePrefix("early ")) ?: continue

            val prefix = text.substring(0, match.range.first).trim()
            val before = prefix.endsWith("before")
            val after = prefix.endsWith("after") || prefix.endsWith("by")

            // The delay can sit on either side: "an hour after dinner" or "dinner by an hour".
            val lead = if (before || after) {
                parseDuration(
                    prefix.substringBeforeLast("before")
                        .substringBeforeLast("after")
                        .substringBeforeLast("by")
                        .trim(),
                ) ?: 0L
            } else {
                0L
            }
            val tail = text.substring(match.range.last + 1).trim()
            val extra = parseDuration(tail.removePrefix("by ").trim()) ?: 0L
            val total = lead + extra

            return TimeReference.Daypart(
                phrase = text.trim(),
                daypart = word,
                offsetMillis = if (before) -total else total,
                before = before && !after,
            )
        }
        return null
    }

    private fun containsWord(text: String, word: String): Boolean =
        Regex("(?:^|[^a-z])${Regex.escape(word)}(?:[^a-z]|$)").containsMatchIn(text)

    /** "today", "tomorrow", "the day after tomorrow", "in 3 days". */
    private fun parseDayOffset(text: String): Int? = when {
        text.contains("day after tomorrow") -> 2
        text.contains("tomorrow") -> 1
        text.contains("today") || text.contains("tonight") -> 0
        else -> {
            val inDays = Regex("in\\s+(\\d+|[a-z]+)\\s+days?").find(text)
            if (inDays != null) {
                val value = inDays.groupValues[1]
                (value.toIntOrNull() ?: Text.wordToNumber(value))
            } else {
                null
            }
        }
    }

    private fun parseDayName(text: String): String? {
        for ((name, _) in WEEKDAYS) {
            if (Regex("(?:^|[^a-z])$name(?:[^a-z]|$)").containsMatchIn(text)) return name
        }
        for (alias in WEEKDAY_ALIASES.keys) {
            if (alias == "weekday" || alias == "weekend") continue
            if (Regex("(?:^|[^a-z])$alias(?:[^a-z]|$)").containsMatchIn(text)) {
                return WEEKDAYS.firstOrNull { it.second == WEEKDAY_ALIASES[alias] }?.first
            }
        }
        return null
    }

    private fun weekdayOffset(dayName: String, text: String, time: ClockTime?): Int {
        val target = WEEKDAYS.firstOrNull { it.first == dayName }?.second
            ?: WEEKDAY_ALIASES[dayName]
            ?: return 0
        val today = clock.localDayOfWeek()
        var ahead = (target - today + 7) % 7
        if (ahead == 0) {
            // "next Monday" said on a Monday means the following week; a bare "Monday" means today
            // if the time has not passed yet, otherwise next week.
            ahead = if (text.contains("next")) 7 else if (timeAlreadyPassed(time)) 7 else 0
        }
        // Otherwise "next Friday" said on a Tuesday already means the coming Friday, which is what
        // `ahead` holds; no adjustment needed.
        return ahead
    }

    private fun timeAlreadyPassed(time: ClockTime?): Boolean {
        if (time == null) return false
        val nowMinutes = clock.localHour() * 60 + clock.localMinute()
        return time.hour * 60 + time.minute <= nowMinutes
    }

    // -------------------------------------------------------------------- resolution ----

    /**
     * Turns a [TimeReference] into an absolute epoch instant.
     *
     * Returns null for [TimeReference.Unresolved] and for a recurring reference (which has no
     * single next fire time without a scheduler); callers use [nextOccurrence] for those.
     *
     * A one-shot time that has already passed is rolled forward to the next day rather than
     * fired immediately: "remind me at 7" said at 9pm means tomorrow at 7, not now.
     */
    fun resolve(reference: TimeReference, nowMillis: Long = clock.nowMillis()): Long? {
        val cfg = config()
        return when (reference) {
            is TimeReference.In -> nowMillis + reference.offsetMillis

            is TimeReference.At -> {
                val candidate = localDayStart(nowMillis) + reference.dayOffset * DAY_MILLIS +
                    reference.hour * HOUR_MILLIS + reference.minute * MINUTE_MILLIS
                if (candidate <= nowMillis && reference.dayOffset == 0) candidate + DAY_MILLIS else candidate
            }

            is TimeReference.Daypart -> {
                val hour = if (reference.daypart == "tonight") {
                    cfg.daypartHour("night") ?: TONIGHT_HOUR
                } else {
                    cfg.daypartHour(reference.daypart)
                        ?: cfg.daypartHour(reference.daypart.removePrefix("early "))
                        ?: return null
                }
                val base = localDayStart(nowMillis) + hour * HOUR_MILLIS + reference.offsetMillis
                if (base <= nowMillis) base + DAY_MILLIS else base
            }

            is TimeReference.RecurringAt -> nextOccurrence(reference.recurrence, nowMillis)
            is TimeReference.Unresolved -> null
        }
    }

    /** Convenience: parse and resolve in one call. */
    fun resolvePhrase(phrase: String, nowMillis: Long = clock.nowMillis()): Long? {
        val reference = parse(phrase) ?: return null
        return resolve(reference, nowMillis)
    }

    /** Next fire time for a recurrence, or null when it has no time-of-day to fire at. */
    fun nextOccurrence(recurrence: Recurrence, nowMillis: Long = clock.nowMillis()): Long? {
        val hour = recurrence.hourOfDay
        return when (recurrence.frequency) {
            Recurrence.Frequency.MINUTELY ->
                nowMillis + (recurrence.intervalMillis ?: MINUTE_MILLIS)
            Recurrence.Frequency.HOURLY -> {
                val base = localDayStart(nowMillis) + clock.localHour(nowMillis) * HOUR_MILLIS +
                    recurrence.minute * MINUTE_MILLIS
                if (base <= nowMillis) base + HOUR_MILLIS else base
            }
            Recurrence.Frequency.DAILY -> {
                if (hour == null) return null
                val candidate = localDayStart(nowMillis) + hour * HOUR_MILLIS +
                    recurrence.minute * MINUTE_MILLIS
                if (candidate <= nowMillis) candidate + DAY_MILLIS else candidate
            }
            Recurrence.Frequency.WEEKLY -> {
                if (hour == null || recurrence.daysOfWeek.isEmpty()) return null
                var best: Long? = null
                for (dayOffset in 0..7) {
                    val candidate = localDayStart(nowMillis) + dayOffset * DAY_MILLIS +
                        hour * HOUR_MILLIS + recurrence.minute * MINUTE_MILLIS
                    if (candidate <= nowMillis) continue
                    if (clock.localDayOfWeek(candidate) in recurrence.daysOfWeek) {
                        best = candidate
                        break
                    }
                }
                best
            }
            Recurrence.Frequency.MONTHLY -> {
                if (hour == null) return null
                // Without a calendar library the month length is not knowable from epoch maths
                // alone, so this approximates with 30 days and says so in the description.
                val candidate = nowMillis + 30 * DAY_MILLIS
                localDayStart(candidate) + hour * HOUR_MILLIS + recurrence.minute * MINUTE_MILLIS
            }
        }
    }

    // ------------------------------------------------------------------- description ----

    /** Human-readable relative/absolute description, e.g. "tomorrow at 7:00 PM" or "in 5 minutes". */
    fun describe(epochMillis: Long, nowMillis: Long = clock.nowMillis()): String {
        val delta = epochMillis - nowMillis
        if (delta in 0 until MINUTE_MILLIS) return "in less than a minute"
        if (delta in 0 until HOUR_MILLIS) return "in ${describeDuration(delta)}"
        if (delta < 0 && abs(delta) < HOUR_MILLIS) return "${describeDuration(abs(delta))} ago"

        val offset = dayOffsetOf(epochMillis, nowMillis)
        val hour = clock.localHour(epochMillis)
        val minute = clock.localMinute(epochMillis)
        val clockText = formatClock(hour, minute)
        return when (offset) {
            0 -> "today at $clockText"
            1 -> "tomorrow at $clockText"
            2 -> "the day after tomorrow at $clockText"
            -1 -> "yesterday at $clockText"
            in 3..6 -> "in $offset days at $clockText"
            else -> "on ${dayNameAt(epochMillis, clock.zoneOffsetSeconds())} at $clockText"
        }
    }
}

/** "7:05 PM" - 12-hour clock, because that is how the user spoke it. */
fun formatClock(hour: Int, minute: Int): String {
    val meridiem = if (hour < 12) "AM" else "PM"
    val displayHour = when {
        hour == 0 -> 12
        hour > 12 -> hour - 12
        else -> hour
    }
    return "%d:%02d %s".format(displayHour, minute, meridiem)
}

fun dayNameAt(epochMillis: Long, zoneOffsetSeconds: Int): String {
    val shifted = epochMillis + zoneOffsetSeconds * 1000L
    val days = Math.floorDiv(shifted, TemporalParser.DAY_MILLIS)
    val index = Math.floorMod(days + 3, 7L).toInt()
    return TemporalParser.WEEKDAYS.first { it.second == index }.first
        .replaceFirstChar { it.uppercase() }
}

fun describeDuration(millis: Long): String {
    if (millis < 1000) return "a moment"
    val seconds = millis / 1000
    val minutes = seconds / 60
    val hours = minutes / 60
    val days = hours / 24
    return when {
        days > 0 && hours % 24 > 0 -> "${plural(days, "day")} ${plural(hours % 24, "hour")}"
        days > 0 -> plural(days, "day")
        hours > 0 && minutes % 60 > 0 -> "${plural(hours, "hour")} ${plural(minutes % 60, "minute")}"
        hours > 0 -> plural(hours, "hour")
        minutes > 0 && seconds % 60 > 0 ->
            "${plural(minutes, "minute")} ${plural(seconds % 60, "second")}"
        minutes > 0 -> plural(minutes, "minute")
        else -> plural(seconds, "second")
    }
}

/** "1 minute" but "2 minutes" - spoken back to the user, so it has to read correctly. */
private fun plural(value: Long, singular: String): String =
    "$value $singular${if (value == 1L) "" else "s"}"

fun describeRecurrence(recurrence: Recurrence): String {
    val time = recurrence.hourOfDay?.let { formatClock(it, recurrence.minute) }
    val days = if (recurrence.daysOfWeek.size == 7) {
        "every day"
    } else if (recurrence.daysOfWeek.isNotEmpty()) {
        recurrence.daysOfWeek.sorted().joinToString(" and ") { index ->
            TemporalParser.WEEKDAYS.first { it.second == index }.first
        }.let { "every $it" }
    } else {
        null
    }
    return when (recurrence.frequency) {
        Recurrence.Frequency.MINUTELY -> "every minute"
        Recurrence.Frequency.HOURLY -> if (time != null) "every hour at ${recurrence.minute} past" else "every hour"
        Recurrence.Frequency.DAILY -> if (time != null) "every day at $time" else "every day"
        Recurrence.Frequency.WEEKLY -> if (days != null && time != null) "$days at $time"
        else days ?: if (time != null) "every week at $time" else "every week"
        Recurrence.Frequency.MONTHLY -> if (time != null) "every month at $time" else "every month"
    }
}
