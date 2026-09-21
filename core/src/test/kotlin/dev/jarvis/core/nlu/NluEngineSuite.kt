package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.model.Recurrence
import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertNotNull
import dev.jarvis.core.testing.assertTrue
import dev.jarvis.core.util.FixedClock

/**
 * End-to-end understanding: sentence in, [ParsedRequest] out.
 *
 * The rule and entity suites cover the parts; this one covers the whole, including the behaviours
 * that only exist at the seam between them:
 *
 *  - **multi-step requests** are decomposed only when each half stands alone;
 *  - **time** is attached to the request and kept out of the task text;
 *  - **missing slots** become notes the agent can turn into a question;
 *  - **conversation context** decides whether "yes" is an answer or a polite lead-in;
 *  - **a model may reorder intents but never invent one**;
 *  - **secret phrases are flagged as sensitive** so nothing downstream persists them.
 */
object NluEngineSuite : Suite("nlu/engine") {

    private const val MINUTE = 60_000L
    private const val HOUR = 3_600_000L
    private const val DAY = 86_400_000L

    /** Tuesday 2026-03-10, 09:30 in Asia/Kolkata (+05:30). */
    private const val NOW = 1_773_115_200_000L
    private const val OFFSET_SECONDS = 19_800
    private const val MIDNIGHT = NOW - (9 * HOUR + 30 * MINUTE)

    private fun engine(
        config: AssistantConfig = AssistantConfig.DEFAULT,
        reranker: NluEngine.IntentReranker? = null,
    ): NluEngine {
        val clock = FixedClock(NOW, OFFSET_SECONDS)
        return NluEngine(
            configProvider = { config },
            temporal = TemporalParser(clock) { config },
            reranker = reranker,
        )
    }

    init {
        // ------------------------------------------------------- single requests ----

        test("a simple command yields an intent and its argument") {
            val parsed = engine().parse("open whatsapp")
            assertEquals(IntentKind.OPEN_APP, parsed.intent)
            assertEquals("whatsapp", parsed.entityValue(EntityKind.APP_NAME))
            assertTrue(parsed.confidence > NluEngine.LOW_CONFIDENCE, "confidence was ${parsed.confidence}")
            assertEquals("open whatsapp", parsed.originalText)
        }

        test("the wake word is stripped but the original wording is kept") {
            val parsed = engine().parse("hey jarvis open settings")
            assertEquals(IntentKind.OPEN_APP, parsed.intent)
            assertEquals("settings", parsed.entityValue(EntityKind.APP_NAME))
            assertEquals("hey jarvis open settings", parsed.originalText, "originalText must be verbatim")
        }

        test("a bare wake word is a greeting, not an empty command") {
            val parsed = engine().parse("hey jarvis")
            assertEquals(IntentKind.GREETING, parsed.intent)
        }

        test("a renamed assistant answers to its new name") {
            val renamed = AssistantConfig.DEFAULT.copy(nickname = "Friday", wakeWord = "Friday")
            val parsed = engine(renamed).parse("hey friday what time is it")
            assertEquals(IntentKind.DEVICE_STATUS, parsed.intent)
        }

        test("an empty request is unknown rather than an exception") {
            val parsed = engine().parse("   ")
            assertEquals(IntentKind.UNKNOWN, parsed.intent)
            assertEquals(0.0, parsed.confidence)
            assertTrue(parsed.notes.any { it.contains("empty") }, "notes were ${parsed.notes}")
        }

        test("an unrecognisable request is unknown, with the reason recorded") {
            val parsed = engine().parse("banana banana banana")
            assertEquals(IntentKind.UNKNOWN, parsed.intent)
            assertTrue(parsed.notes.any { it.contains("no rule matched") }, "notes were ${parsed.notes}")
        }

        test("small talk is recognised as small talk and flagged as low confidence") {
            val parsed = engine().parse("tell me a joke")
            assertEquals(IntentKind.CHITCHAT, parsed.intent)
            assertTrue(parsed.confidence < NluEngine.LOW_CONFIDENCE)
            assertTrue(parsed.notes.any { it.contains("low confidence") }, "notes were ${parsed.notes}")
        }

        test("the source of the request is carried through") {
            assertTrue(engine().parse("open whatsapp", fromVoice = true).fromVoice)
            assertTrue(!engine().parse("open whatsapp").fromVoice)
        }

        // ------------------------------------------------------------ multi-step ----

        test("two commands joined by 'and then' become two steps") {
            val parsed = engine().parse("set a timer for 10 minutes and then turn on the torch")
            assertTrue(parsed.isMultiStep, "expected a multi-step request, got $parsed")
            val steps = assertNotNull(parsed.steps)
            assertEquals(2, steps.size)
            assertEquals(IntentKind.START_TIMER, steps[0].intent)
            assertEquals(IntentKind.CHANGE_SETTING, steps[1].intent)
            assertEquals("torch", steps[1].entityValue(EntityKind.SETTING))
        }

        test("each step keeps its own arguments and timing") {
            val parsed = engine().parse("remind me to call mom in 5 minutes and then open whatsapp")
            val steps = assertNotNull(parsed.steps)
            assertEquals(2, steps.size)
            assertEquals(IntentKind.CREATE_REMINDER, steps[0].intent)
            assertEquals("call mom", steps[0].entityValue(EntityKind.QUERY))
            assertEquals(IntentKind.OPEN_APP, steps[1].intent)
            assertEquals("whatsapp", steps[1].entityValue(EntityKind.APP_NAME))
        }

        test("the README's example sentence parses as documented") {
            // This is the exchange quoted in README.md, so the README claim is a checked one
            // rather than an aspiration: two steps, the reminder text without its time clause,
            // and "tomorrow at 9am" resolved against the fixed clock.
            val parsed = engine().parse(
                "hey jarvis, remind me to submit the lab report tomorrow at 9am and then open the camera",
            )
            val steps = assertNotNull(parsed.steps)
            assertEquals(2, steps.size)

            val reminder = steps[0]
            assertEquals(IntentKind.CREATE_REMINDER, reminder.intent)
            assertEquals("submit the lab report", reminder.entityValue(EntityKind.QUERY))
            val at = assertNotNull(reminder.time) as TimeReference.At
            assertEquals(1, at.dayOffset)
            assertEquals(9, at.hour)
            assertEquals((MIDNIGHT + DAY + 9 * HOUR).toString(), reminder.entityValue(EntityKind.TIME))

            val app = steps[1]
            assertEquals(IntentKind.OPEN_APP, app.intent)
            assertEquals("camera", app.entityValue(EntityKind.APP_NAME))
        }

        test("flatten lists every leaf step in order") {
            val parsed = engine().parse("open chrome and then scroll down and then go back")
            val leaves = parsed.flatten()
            assertTrue(leaves.size >= 2, "expected several leaves, got ${leaves.map { it.intent }}")
            assertEquals(IntentKind.OPEN_APP, leaves.first().intent)
            assertEquals(leaves.minOf { it.confidence }, parsed.weakestConfidence)
        }

        test("a conjunction inside one request does not split it") {
            // "sonu" on its own is not a request, so the split is rejected and the whole
            // sentence stays one MEDIA_PLAY.
            val parsed = engine().parse("play songs by arjit and sonu")
            assertTrue(!parsed.isMultiStep, "expected a single request, got $parsed")
            assertEquals(IntentKind.MEDIA_PLAY, parsed.intent)
        }

        // ------------------------------------------------------------------ time ----

        test("a relative reminder keeps its delay and drops it from the task text") {
            val parsed = engine().parse("remind me to call mom in 5 minutes")
            assertEquals(IntentKind.CREATE_REMINDER, parsed.intent)
            val time = assertNotNull(parsed.time)
            assertTrue(time is TimeReference.In, "expected a delay, got $time")
            assertEquals(5 * MINUTE, (time as TimeReference.In).offsetMillis)
            assertEquals("call mom", parsed.entityValue(EntityKind.QUERY), "the time belongs to `time`")
            assertEquals(NOW + 5 * MINUTE, TemporalParser(FixedClock(NOW, OFFSET_SECONDS)).resolve(time))
        }

        test("a timer is a duration, not a point in time") {
            val parsed = engine().parse("set a timer for 10 minutes")
            assertEquals(IntentKind.START_TIMER, parsed.intent)
            assertEquals(10 * MINUTE, parsed.durationMillis)
            assertEquals("600000", parsed.entityValue(EntityKind.DURATION))
            assertEquals(null, parsed.time, "a countdown has no clock time")
        }

        test("an absolute time resolves to the instant the user meant") {
            val parsed = engine().parse("wake me at 7am tomorrow")
            assertEquals(IntentKind.CREATE_ALARM, parsed.intent)
            val at = assertNotNull(parsed.time) as TimeReference.At
            assertEquals(1, at.dayOffset)
            assertEquals(7, at.hour)
            assertEquals((MIDNIGHT + DAY + 7 * HOUR).toString(), parsed.entityValue(EntityKind.TIME))
        }

        test("a recurring reminder keeps its recurrence") {
            val parsed = engine().parse("remind me to stretch every day at 9am")
            assertEquals(IntentKind.CREATE_REMINDER, parsed.intent)
            val recurrence = assertNotNull(parsed.recurrence)
            assertEquals(Recurrence.Frequency.DAILY, recurrence.frequency)
            assertEquals(9, recurrence.hourOfDay)
            assertEquals("stretch", parsed.entityValue(EntityKind.QUERY))
        }

        test("a vague time is reported, never defaulted to now") {
            val parsed = engine().parse("remind me to call mom later")
            assertEquals(IntentKind.CREATE_REMINDER, parsed.intent)
            assertTrue(
                parsed.time is TimeReference.Unresolved,
                "an unparsable time must stay unresolved, got ${parsed.time}",
            )
            assertTrue(
                parsed.notes.any { it.contains("could not understand the time") },
                "notes were ${parsed.notes}",
            )
        }

        // ------------------------------------------------------------ missing slots ----

        test("a request with no app named says so instead of guessing") {
            val parsed = engine().parse("open")
            assertEquals(IntentKind.OPEN_APP, parsed.intent)
            assertEquals(null, parsed.entityValue(EntityKind.APP_NAME))
            assertTrue(parsed.notes.any { it.contains("no app name") }, "notes were ${parsed.notes}")
        }

        test("a reminder with nothing in it says so") {
            val parsed = engine().parse("remind me")
            assertEquals(IntentKind.CREATE_REMINDER, parsed.intent)
            assertTrue(
                parsed.notes.any { it.contains("no reminder time or content") },
                "notes were ${parsed.notes}",
            )
        }

        test("a rename with no name says so") {
            val parsed = engine().parse("change your name")
            assertEquals(IntentKind.RENAME_ASSISTANT, parsed.intent)
            assertTrue(parsed.notes.any { it.contains("no new name") }, "notes were ${parsed.notes}")
        }

        test("a complete rename carries the new name") {
            val parsed = engine().parse("from now on your name is friday")
            assertEquals(IntentKind.RENAME_ASSISTANT, parsed.intent)
            assertEquals("friday", parsed.entityValue(EntityKind.ASSISTANT_NAME))
        }

        // ------------------------------------------------------------- ambiguity ----

        test("competing readings are reported as alternatives") {
            val parsed = engine().parse("remind me to note that I prefer tea")
            assertTrue(parsed.alternatives.size >= 1, "expected alternatives, got ${parsed.alternatives}")
            assertTrue(
                parsed.alternatives.any { it.kind != parsed.intent },
                "alternatives must offer a different reading",
            )
            assertTrue(
                parsed.alternatives.all { it.reason.isNotBlank() },
                "each alternative must carry its evidence",
            )
        }

        // ------------------------------------------------------ conversation state ----

        test("'yes' confirms only when a confirmation was asked for") {
            val engine = engine()
            assertEquals(
                IntentKind.CONFIRM,
                engine.parse("yes", context = NluEngine.NluContext(awaitingConfirmation = true)).intent,
            )
            // With nothing pending, "yes open settings" is a request that starts politely.
            assertEquals(IntentKind.OPEN_APP, engine.parse("yes open settings").intent)
        }

        test("a bare 'yes' with nothing pending stays a confirmation") {
            // One or two tokens cannot hide a request, so the polite reading wins.
            assertEquals(IntentKind.CONFIRM, engine().parse("yes").intent)
            assertEquals(IntentKind.CANCEL, engine().parse("cancel").intent)
        }

        test("during a lesson an unrecognisable reply is an answer") {
            val parsed = engine().parse(
                "paris",
                context = NluEngine.NluContext(activeLesson = true),
            )
            assertEquals(IntentKind.ANSWER_QUESTION, parsed.intent)
            assertEquals("paris", parsed.entityValue(EntityKind.QUERY))
        }

        test("outside a lesson the same reply is simply unknown") {
            assertEquals(IntentKind.UNKNOWN, engine().parse("paris").intent)
        }

        // ---------------------------------------------------------------- privacy ----

        test("a secret phrase is flagged sensitive and carries no entities") {
            val parsed = engine().parse("my secret phrase is open sesame")
            assertEquals(IntentKind.AUTHENTICATE, parsed.intent)
            assertTrue(parsed.sensitive, "authentication requests must not be persisted")
            assertTrue(parsed.entities.isEmpty(), "the phrase must not be copied into an entity")
        }

        test("opting out of memory is flagged sensitive too") {
            val parsed = engine().parse("don't remember this conversation")
            assertEquals(IntentKind.DO_NOT_REMEMBER, parsed.intent)
            assertTrue(parsed.sensitive)
        }

        test("an ordinary request is not flagged sensitive") {
            assertTrue(!engine().parse("open whatsapp").sensitive)
        }

        // ------------------------------------------------------ the model seam ----

        test("a reranker may reorder the candidates") {
            val reorder = NluEngine.IntentReranker { _, candidates -> candidates.reversed() }
            val text = "remind me to note that I prefer tea"
            val expected = IntentRules.match(text, AssistantConfig.DEFAULT).last().kind
            assertEquals(expected, engine(reranker = reorder).parse(text).intent)
        }

        test("a reranker cannot invent an intent no rule recognised") {
            val invented = NluEngine.IntentReranker { _, candidates ->
                listOf(
                    IntentCandidate(IntentKind.FORGET_ALL, 0.99, "invented by a misbehaving model"),
                ) + candidates
            }
            val parsed = engine(reranker = invented).parse("open whatsapp")
            assertEquals(IntentKind.OPEN_APP, parsed.intent, "an invented intent must be discarded")
        }

        test("a failing reranker degrades to the deterministic result") {
            val broken = NluEngine.IntentReranker { _, _ -> throw IllegalStateException("model unavailable") }
            assertEquals(IntentKind.OPEN_APP, engine(reranker = broken).parse("open whatsapp").intent)
        }

        // ------------------------------------------------------------- regressions ----

        test("a capability question is not treated as an unresolved time") {
            val parsed = engine().parse("what can you do")
            assertEquals(IntentKind.ASK_CAPABILITIES, parsed.intent)
            assertEquals(null, parsed.time)
            assertTrue(
                parsed.notes.none { it.contains("could not understand the time") },
                "notes were ${parsed.notes}",
            )
        }

        test("'turn on wifi' is a setting change with no time attached") {
            val parsed = engine().parse("turn on wifi")
            assertEquals(IntentKind.CHANGE_SETTING, parsed.intent)
            assertEquals("wifi", parsed.entityValue(EntityKind.SETTING))
            assertEquals("on", parsed.entityValue(EntityKind.SETTING_VALUE))
            assertEquals(null, parsed.time)
        }

        test("forgetting everything is not read as forgetting a topic") {
            val parsed = engine().parse("forget everything")
            assertEquals(IntentKind.FORGET_ALL, parsed.intent)
        }

        test("a message request is recognised with only a name after it") {
            val parsed = engine().parse("text rahul")
            assertEquals(IntentKind.SEND_MESSAGE, parsed.intent)
            assertEquals("rahul", parsed.entityValue(EntityKind.CONTACT))
        }
    }
}
