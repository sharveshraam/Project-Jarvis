package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.model.Recurrence
import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertNotNull
import dev.jarvis.core.testing.assertNull
import dev.jarvis.core.testing.assertTrue
import dev.jarvis.core.util.FixedClock

/**
 * Time understanding.
 *
 * Time is the one part of natural language where a wrong guess is actively harmful: a reminder an
 * hour early is missed, and one that fires immediately because the parser could not resolve the
 * phrase is worse than useless. These tests therefore pin down both the parsing *and* the refusal
 * to parse - an unresolvable time must come back as [TimeReference.Unresolved], never as "now".
 *
 * Every test runs against a fixed clock at a known local instant, so expected values are computed
 * from first principles here rather than read back from the parser.
 */
object TemporalSuite : Suite("nlu/temporal") {

    private const val MINUTE = 60_000L
    private const val HOUR = 3_600_000L
    private const val DAY = 86_400_000L

    /** Tuesday 2026-03-10, 09:30 in Asia/Kolkata (+05:30). */
    private const val NOW = 1_773_115_200_000L
    private const val OFFSET_SECONDS = 19_800

    /** Local midnight of that Tuesday: NOW minus 9 hours 30 minutes. */
    private const val MIDNIGHT = NOW - (9 * HOUR + 30 * MINUTE)

    private fun parser(config: AssistantConfig = AssistantConfig.DEFAULT): TemporalParser =
        TemporalParser(FixedClock(NOW, OFFSET_SECONDS)) { config }

    init {
        // ------------------------------------------------------------- durations ----

        test("relative offsets parse and resolve against now") {
            val p = parser()
            val inFive = p.parse("in 5 minutes")
            assertTrue(inFive is TimeReference.In, "expected a relative offset, got $inFive")
            assertEquals(5 * MINUTE, (inFive as TimeReference.In).offsetMillis)
            assertEquals(NOW + 5 * MINUTE, p.resolve(inFive))
        }

        test("hours, days and weeks resolve to the right span") {
            val p = parser()
            assertEquals(2 * HOUR, (p.parse("in 2 hours") as TimeReference.In).offsetMillis)
            assertEquals(2 * DAY, (p.parse("in two days") as TimeReference.In).offsetMillis)
            assertEquals(21 * DAY, (p.parse("in 3 weeks") as TimeReference.In).offsetMillis)
            assertEquals(90_000L, (p.parse("in 90 seconds") as TimeReference.In).offsetMillis)
        }

        test("articles and word numbers are understood") {
            val p = parser()
            assertEquals(HOUR, p.parseDuration("an hour"))
            assertEquals(MINUTE, p.parseDuration("a minute"))
            assertEquals(1_000L, p.parseDuration("one second"))
            assertEquals(3 * HOUR, p.parseDuration("three hours"))
        }

        test("fractional and compound durations are exact") {
            val p = parser()
            assertEquals(90 * MINUTE, p.parseDuration("1.5 hours"), "1.5h must not truncate to 1h")
            assertEquals(30 * MINUTE, p.parseDuration("half an hour"))
            assertEquals(90 * MINUTE + 30_000L, p.parseDuration("1 hour 30 minutes 30 seconds"))
        }

        test("duration phrases carry their preposition") {
            val p = parser()
            assertEquals(10 * MINUTE, p.parseDuration("for 10 minutes"))
            assertEquals(10 * MINUTE, p.parseDuration("after 10 minutes"))
            assertEquals(45 * MINUTE, p.parseDuration("45 minutes long"))
        }

        test("text with no duration in it yields nothing rather than zero") {
            val p = parser()
            assertNull(p.parseDuration("open whatsapp"))
            assertNull(p.parseDuration("banana"))
            assertNull(p.parseDuration(""))
        }

        // ----------------------------------------------------------- clock times ----

        test("an explicit evening time beats the daypart word next to it") {
            val p = parser()
            val ref = p.parse("at half past seven in the evening")
            assertTrue(ref is TimeReference.At, "expected a clock time, got $ref")
            ref as TimeReference.At
            assertEquals(19, ref.hour, "'seven in the evening' is 19:00")
            assertEquals(30, ref.minute)
            assertEquals(MIDNIGHT + 19 * HOUR + 30 * MINUTE, p.resolve(ref))
        }

        test("quarter past and quarter to are read correctly") {
            val p = parser()
            assertEquals(15, (p.parseClockTime("quarter past nine") ?: error("no time")).minute)
            assertEquals(9, p.parseClockTime("quarter past nine")?.hour)
            val to = p.parseClockTime("quarter to eight in the morning")
            assertEquals(7, to?.hour, "'quarter to eight' is 07:45")
            assertEquals(45, to?.minute)
        }

        test("24-hour input is taken literally") {
            val p = parser()
            val ref = p.parse("at 14:30") as TimeReference.At
            assertEquals(14, ref.hour)
            assertEquals(30, ref.minute)
            assertEquals(MIDNIGHT + 14 * HOUR + 30 * MINUTE, p.resolve(ref))
        }

        test("noon and midnight are exact") {
            val p = parser()
            assertEquals(12, p.parseClockTime("at noon")?.hour)
            assertEquals(0, p.parseClockTime("at midnight")?.hour)
        }

        test("a future time today stays today, a passed time rolls to tomorrow") {
            val p = parser()
            val evening = p.parse("at 7pm") as TimeReference.At
            assertEquals(0, evening.dayOffset)
            assertEquals(MIDNIGHT + 19 * HOUR, p.resolve(evening), "7pm today is still ahead of 09:30")

            val morning = p.parse("at 7am") as TimeReference.At
            assertEquals(MIDNIGHT + DAY + 7 * HOUR, p.resolve(morning), "7am today has passed")
        }

        test("an unqualified hour picks the next plausible occurrence") {
            val p = parser()
            // Said at 09:30, "at 7" cannot mean 07:00 today - that has passed - so it means 19:00.
            val ref = p.parse("remind me at 7") as TimeReference.At
            assertEquals(19, ref.hour)
            assertEquals(MIDNIGHT + 19 * HOUR, p.resolve(ref))
        }

        test("day names are counted forward from today") {
            val p = parser()
            // Today is Tuesday, so Friday is 3 days away and Monday is 6.
            val friday = p.parse("on friday at 6pm") as TimeReference.At
            assertEquals(3, friday.dayOffset)
            assertEquals(18, friday.hour)
            assertEquals("friday", friday.dayName)
            assertEquals(MIDNIGHT + 3 * DAY + 18 * HOUR, p.resolve(friday))

            val monday = p.parse("monday") as TimeReference.At
            assertEquals(6, monday.dayOffset)
        }

        test("tomorrow and the day after tomorrow are offset by whole days") {
            val p = parser()
            assertEquals(1, (p.parse("tomorrow at 9") as TimeReference.At).dayOffset)
            assertEquals(2, (p.parse("the day after tomorrow") as TimeReference.At).dayOffset)
            assertEquals(0, (p.parse("today at 11") as TimeReference.At).dayOffset)
        }

        test("a named day with no time uses the user's configured morning") {
            val config = AssistantConfig.DEFAULT.copy(
                daypartHours = AssistantConfig.DEFAULT_DAYPARTS.toMutableMap().apply { put("morning", 6) },
            )
            val ref = parser(config).parse("on sunday") as TimeReference.At
            assertEquals(6, ref.hour, "the configured morning hour must be honoured")
        }

        test("a named day plus a daypart keeps the day offset") {
            val p = parser()
            val ref = p.parse("tomorrow evening")
            assertTrue(ref is TimeReference.At, "'tomorrow evening' is a specific day, got $ref")
            ref as TimeReference.At
            assertEquals(1, ref.dayOffset, "must not collapse onto today")
            assertEquals(18, ref.hour, "evening is 18:00 by default")
        }

        // ------------------------------------------------------------- dayparts ----

        test("dayparts come from the user's own schedule") {
            val config = AssistantConfig.DEFAULT.copy(
                daypartHours = AssistantConfig.DEFAULT_DAYPARTS.toMutableMap().apply { put("evening", 17) },
            )
            val p = parser(config)
            val ref = p.parse("this evening") as TimeReference.Daypart
            assertEquals("evening", ref.daypart)
            assertEquals(MIDNIGHT + 17 * HOUR, p.resolve(ref), "the customised hour must be used")
        }

        test("after a daypart adds the stated delay") {
            val p = parser()
            val ref = p.parse("an hour after lunch") as TimeReference.Daypart
            assertEquals("lunch", ref.daypart)
            assertEquals(HOUR, ref.offsetMillis)
            // Default lunch is 13:00, so an hour after is 14:00 today.
            assertEquals(MIDNIGHT + 14 * HOUR, p.resolve(ref))
        }

        test("before a daypart is marked as before, not as a negative delay") {
            val p = parser()
            val ref = p.parse("before breakfast") as TimeReference.Daypart
            assertTrue(ref.before, "must be flagged so it can be spoken back as 'before breakfast'")
            assertEquals(0L, ref.offsetMillis)
            // Breakfast is 08:00, which has passed at 09:30, so it rolls to tomorrow.
            assertEquals(MIDNIGHT + DAY + 8 * HOUR, p.resolve(ref))
        }

        test("tonight resolves from the configured night hour") {
            val p = parser()
            val ref = p.parse("tonight") as TimeReference.Daypart
            assertEquals(MIDNIGHT + 22 * HOUR, p.resolve(ref))
        }

        // ---------------------------------------------------------- recurrence ----

        test("a daily recurrence keeps its time of day") {
            val p = parser()
            val ref = p.parse("every day at 7am") as TimeReference.RecurringAt
            assertEquals(Recurrence.Frequency.DAILY, ref.recurrence.frequency)
            assertEquals(7, ref.recurrence.hourOfDay)
            // 07:00 today has passed, so the next fire time is tomorrow morning.
            assertEquals(MIDNIGHT + DAY + 7 * HOUR, p.nextOccurrence(ref.recurrence))
        }

        test("a weekly recurrence names its weekday") {
            val p = parser()
            val ref = p.parse("every monday at 9am") as TimeReference.RecurringAt
            assertEquals(Recurrence.Frequency.WEEKLY, ref.recurrence.frequency)
            assertEquals(setOf(0), ref.recurrence.daysOfWeek, "Monday is index 0")
            val next = assertNotNull(p.nextOccurrence(ref.recurrence))
            assertEquals(MIDNIGHT + 6 * DAY + 9 * HOUR, next, "next Monday is 6 days from Tuesday")
        }

        test("an interval recurrence keeps its interval instead of firing every minute") {
            val p = parser()
            val ref = p.parse("every 30 minutes") as TimeReference.RecurringAt
            assertEquals(Recurrence.Frequency.MINUTELY, ref.recurrence.frequency)
            assertEquals(30 * MINUTE, ref.recurrence.intervalMillis)
            assertEquals(NOW + 30 * MINUTE, p.nextOccurrence(ref.recurrence))
        }

        test("recurrences have no single resolution instant") {
            val p = parser()
            val ref = p.parse("every day at 7am") as TimeReference.RecurringAt
            // resolve() does give the next fire time, which is what a one-shot scheduler wants;
            // describe() must still read as a recurrence rather than a date.
            assertEquals("every day at 7:00 AM", ref.describe())
            assertEquals(MIDNIGHT + DAY + 7 * HOUR, p.resolve(ref))
        }

        // ------------------------------------------------------- refusing to guess ----

        test("a vague time is unresolved, never defaulted to now") {
            val p = parser()
            val ref = p.parse("remind me later")
            assertTrue(ref is TimeReference.Unresolved, "expected Unresolved, got $ref")
            assertNull(p.resolve(ref!!), "an unresolved time must not produce an instant")
        }

        test("text with no time in it parses to nothing at all") {
            val p = parser()
            assertNull(p.parse("open whatsapp"))
            assertNull(p.parse("what can you do"))
        }

        // ------------------------------------------------------------- speaking ----

        test("resolution is described the way a person would say it") {
            val p = parser()
            assertEquals("in 5 minutes", p.describe(NOW + 5 * MINUTE))
            assertEquals("today at 7:00 PM", p.describe(MIDNIGHT + 19 * HOUR))
            assertEquals("tomorrow at 7:00 AM", p.describe(MIDNIGHT + DAY + 7 * HOUR))
            assertEquals("5 minutes ago", p.describe(NOW - 5 * MINUTE))
            assertEquals("in less than a minute", p.describe(NOW + 20_000L))
        }

        test("far-future instants are described by weekday name") {
            val p = parser()
            // 9 days out is beyond the "in N days" window, so it names the day: a Thursday.
            assertEquals("on Thursday at 9:30 AM", p.describe(NOW + 9 * DAY))
        }

        test("clock formatting is 12-hour because that is how it was spoken") {
            assertEquals("12:05 AM", formatClock(0, 5))
            assertEquals("7:05 PM", formatClock(19, 5))
            assertEquals("12:00 PM", formatClock(12, 0))
            assertEquals("1:45 PM", formatClock(13, 45))
        }

        test("durations are described in the largest sensible units") {
            assertEquals("a moment", describeDuration(500))
            assertEquals("1 minute", describeDuration(MINUTE))
            assertEquals("5 minutes", describeDuration(5 * MINUTE))
            assertEquals("1 minute 30 seconds", describeDuration(90_000L))
            assertEquals("1 hour 30 minutes", describeDuration(90 * MINUTE))
            assertEquals("1 day", describeDuration(DAY))
            assertEquals("2 days 3 hours", describeDuration(2 * DAY + 3 * HOUR))
        }

        test("recurrences are described in plain language") {
            assertEquals(
                "every day at 7:00 AM",
                describeRecurrence(Recurrence(Recurrence.Frequency.DAILY, hourOfDay = 7)),
            )
            assertEquals(
                "every monday and friday at 6:00 PM",
                describeRecurrence(
                    Recurrence(
                        Recurrence.Frequency.WEEKLY,
                        daysOfWeek = setOf(4, 0),
                        hourOfDay = 18,
                    ),
                ),
            )
            assertEquals(
                "every hour",
                describeRecurrence(Recurrence(Recurrence.Frequency.HOURLY)),
            )
        }

        test("day offsets are counted in local days, not 24-hour blocks") {
            val p = parser()
            assertEquals(0, p.dayOffsetOf(MIDNIGHT + 23 * HOUR))
            assertEquals(1, p.dayOffsetOf(MIDNIGHT + DAY + 1 * MINUTE))
            assertEquals(-1, p.dayOffsetOf(MIDNIGHT - 1 * MINUTE))
        }

        test("the day name matches the fixed clock's date") {
            assertEquals("Tuesday", dayNameAt(NOW, OFFSET_SECONDS))
            assertEquals("Wednesday", dayNameAt(NOW + DAY, OFFSET_SECONDS))
        }
    }
}
