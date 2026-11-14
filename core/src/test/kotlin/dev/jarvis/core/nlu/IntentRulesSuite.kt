package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertNotNull
import dev.jarvis.core.testing.assertNull
import dev.jarvis.core.testing.assertTrue

/**
 * Intent classification.
 *
 * These tests are written as *user sentences*, not as pattern checks, because the contract that
 * matters is "when someone says this, Jarvis understands that". Every rule in the table has at
 * least one sentence here, and the look-alike pairs that are easiest to confuse (remember /
 * recall / forget, reminder / timer / alarm) get explicit tests.
 *
 * The last group covers the sentences that must NOT match anything. Inventing an intent is worse
 * than admitting confusion, so "no match" is behaviour worth pinning down.
 */
object IntentRulesSuite : Suite("nlu/intent-rules") {

    private val config = AssistantConfig.DEFAULT

    private fun best(text: String, cfg: AssistantConfig = config): IntentKind? =
        IntentRules.match(text, cfg).firstOrNull()?.kind

    private fun kinds(text: String, cfg: AssistantConfig = config): List<IntentKind> =
        IntentRules.match(text, cfg).map { it.kind }

    init {
        // --------------------------------------------------------------- memory ----

        test("the four memory phrasings are told apart") {
            assertEquals(IntentKind.REMEMBER, best("remember that I prefer dark mode"))
            assertEquals(IntentKind.QUERY_MEMORY, best("what do you remember about my thesis"))
            assertEquals(IntentKind.LIST_MEMORIES, best("what do you remember about me"))
            assertEquals(IntentKind.FORGET_TOPIC, best("forget my card number"))
        }

        test("forgetting everything outranks forgetting a topic") {
            assertEquals(IntentKind.FORGET_ALL, best("forget everything you know about me"))
            assertEquals(IntentKind.FORGET_ALL, best("delete all my memories"))
            assertEquals(IntentKind.FORGET_ALL, best("erase all data"))
        }

        test("opting out of memory is its own intent") {
            assertEquals(IntentKind.DO_NOT_REMEMBER, best("don't remember this conversation"))
            assertEquals(IntentKind.DO_NOT_REMEMBER, best("never save what I say in private mode"))
        }

        test("a stated preference is recognised as one") {
            assertEquals(IntentKind.SET_PREFERENCE, best("I prefer dark mode"))
        }

        // ------------------------------------------------------- reminders, timers ----

        test("reminder, timer and alarm are distinct") {
            assertEquals(IntentKind.CREATE_REMINDER, best("remind me to call mom in 5 minutes"))
            assertEquals(IntentKind.START_TIMER, best("set a timer for 10 minutes"))
            assertEquals(IntentKind.CREATE_ALARM, best("wake me at 7am tomorrow"))
            assertEquals(IntentKind.CANCEL_TIMER, best("cancel my timer"))
        }

        test("tasks and notes are distinct from reminders") {
            assertEquals(IntentKind.CREATE_TASK, best("add buy milk to my to-do list"))
            assertEquals(IntentKind.COMPLETE_TASK, best("mark the laundry task as done"))
            assertEquals(IntentKind.LIST_TASKS, best("what are my tasks"))
            assertEquals(IntentKind.SUMMARIZE_TASKS, best("summarise my tasks for the week"))
            assertEquals(IntentKind.CREATE_NOTE, best("take a note that the wifi password changed"))
            assertEquals(IntentKind.LIST_NOTES, best("show my notes"))
        }

        // ------------------------------------------------------ apps and the screen ----

        test("opening an app is recognised") {
            assertEquals(IntentKind.OPEN_APP, best("open whatsapp"))
            assertEquals(IntentKind.OPEN_APP, best("launch the camera"))
            assertEquals(IntentKind.OPEN_APP, best("take me to settings"))
        }

        test("screen-agent verbs map to screen intents") {
            assertEquals(IntentKind.READ_SCREEN, best("read the screen to me"))
            assertEquals(IntentKind.READ_SCREEN, best("what's on the screen"))
            assertEquals(IntentKind.TAP_ELEMENT, best("click the send button"))
            assertEquals(IntentKind.TYPE_TEXT, best("type 'hello there'"))
            assertEquals(IntentKind.SCROLL, best("scroll down"))
            assertEquals(IntentKind.GO_BACK, best("go back"))
            assertEquals(IntentKind.GO_HOME, best("go home"))
        }

        test("an ordinal choice beats a generic tap") {
            // "the second one" is a specific node; TAP_ELEMENT would leave the agent guessing.
            assertEquals(IntentKind.SELECT_ORDINAL, best("tap the second one"))
            assertEquals(IntentKind.SELECT_ORDINAL, best("the 3rd"))
        }

        test("searching inside an app is not a web search") {
            assertEquals(IntentKind.SEARCH_IN_APP, best("search for kerala weather in chrome"))
            assertEquals(IntentKind.WEB_SEARCH, best("search the web for kerala weather"))
        }

        // ------------------------------------------------------------------- media ----

        test("media verbs are split rather than lumped together") {
            assertEquals(IntentKind.MEDIA_PAUSE, best("pause the music"))
            assertEquals(IntentKind.MEDIA_RESUME, best("resume playback"))
            assertEquals(IntentKind.MEDIA_STOP, best("stop the music"))
            assertEquals(IntentKind.MEDIA_NEXT, best("next track"))
            assertEquals(IntentKind.MEDIA_PREVIOUS, best("play the previous song"))
            assertEquals(IntentKind.MEDIA_SKIP, best("skip forward 30 seconds"))
            assertEquals(IntentKind.MEDIA_STATUS, best("what's playing"))
            assertEquals(IntentKind.MEDIA_PLAY, best("play something relaxing"))
            assertEquals(IntentKind.SET_VOLUME, best("set the volume to 40 percent"))
        }

        test("'press play' is media, not a screen tap") {
            assertEquals(IntentKind.MEDIA_PLAY, best("press play"))
        }

        // --------------------------------------------------------- device and settings ----

        test("device questions are status, not commands") {
            assertEquals(IntentKind.DEVICE_STATUS, best("what time is it"))
            assertEquals(IntentKind.DEVICE_STATUS, best("how much battery is left"))
            assertEquals(IntentKind.DEVICE_STATUS, best("what's the date today"))
            assertEquals(IntentKind.DEVICE_STATUS, best("how much storage do I have"))
        }

        test("toggles and adjustments are setting changes") {
            assertEquals(IntentKind.CHANGE_SETTING, best("turn on the torch"))
            assertEquals(IntentKind.CHANGE_SETTING, best("enable dark mode"))
            assertEquals(IntentKind.CHANGE_SETTING, best("turn off wifi"))
            assertEquals(IntentKind.CHANGE_SETTING, best("set brightness to 70 percent"))
            assertEquals(IntentKind.CHANGE_SETTING, best("lock my phone"))
        }

        // ------------------------------------------------- assistant configuration ----

        test("renaming the assistant is recognised however it is phrased") {
            assertEquals(IntentKind.RENAME_ASSISTANT, best("from now on your name is Friday"))
            assertEquals(IntentKind.RENAME_ASSISTANT, best("I'll call you Boss"))
            assertEquals(IntentKind.RENAME_ASSISTANT, best("rename yourself to Atom"))
        }

        test("capability questions go to the capability intent") {
            assertEquals(IntentKind.ASK_CAPABILITIES, best("what can you do"))
            assertEquals(IntentKind.ASK_CAPABILITIES, best("can you read my screen"))
            assertEquals(IntentKind.ASK_CAPABILITIES, best("help"))
            assertEquals(IntentKind.ASK_CAPABILITIES, best("show your capabilities"))
        }

        test("privacy and data control are recognised") {
            assertEquals(IntentKind.PRIVACY_CONTROL, best("export my data"))
            assertEquals(IntentKind.PRIVACY_CONTROL, best("are you listening right now"))
            assertEquals(IntentKind.PRIVACY_CONTROL, best("what do you store about me"))
            assertEquals(IntentKind.PRIVACY_CONTROL, best("what permissions do you have"))
        }

        test("the secret phrase is recognised as authentication") {
            assertEquals(IntentKind.AUTHENTICATE, best("my secret phrase is open sesame"))
            assertEquals(IntentKind.AUTHENTICATE, best("unlock private mode"))
        }

        // ------------------------------------------------------- teaching and knowledge ----

        test("teaching mode and learning progress are distinct") {
            assertEquals(IntentKind.TEACH, best("teach me about mesh analysis"))
            assertEquals(IntentKind.TEACH, best("start teaching mode"))
            assertEquals(IntentKind.CONTINUE_LESSON, best("continue the lesson"))
            assertEquals(IntentKind.LEARNING_PROGRESS, best("how am I doing"))
            assertEquals(IntentKind.LEARNING_PROGRESS, best("what have I learned so far"))
        }

        test("knowledge ingestion and retrieval are distinct") {
            assertEquals(IntentKind.INGEST_KNOWLEDGE, best("import this pdf into your knowledge"))
            assertEquals(IntentKind.QUERY_KNOWLEDGE, best("search my notes for Kirchhoff"))
            assertEquals(IntentKind.EXPLAIN, best("explain the difference between ac and dc"))
        }

        // -------------------------------------------------------------- automation ----

        test("automation phrasing is recognised") {
            assertEquals(IntentKind.CREATE_AUTOMATION, best("whenever I plug in the charger then turn on wifi"))
            assertEquals(IntentKind.CREATE_AUTOMATION, best("create a rule that mutes my phone at night"))
            assertEquals(IntentKind.LIST_AUTOMATIONS, best("show my automations"))
            assertEquals(IntentKind.TOGGLE_AUTOMATION, best("disable that automation"))
            assertEquals(IntentKind.DELETE_AUTOMATION, best("delete the morning rule"))
        }

        // ------------------------------------------------------------- communication ----

        test("messaging and calls are recognised even though they are not permitted") {
            // These intents exist so the engine can explain the limitation honestly. Falling
            // through to CHITCHAT would hide the fact that the request was understood.
            assertEquals(IntentKind.MAKE_CALL, best("call mom"))
            assertEquals(IntentKind.SEND_MESSAGE, best("send a text to Rahul"))
            assertEquals(IntentKind.READ_MESSAGES, best("read my messages"))
            assertEquals(IntentKind.READ_NOTIFICATIONS, best("any new notifications"))
            assertEquals(IntentKind.REPLY_TO_LATEST, best("reply to that"))
        }

        test("call idioms are not phone calls") {
            assertTrue(best("what do you call this") != IntentKind.MAKE_CALL)
            assertTrue(best("I'll call you later") != IntentKind.MAKE_CALL)
        }

        // ------------------------------------------------------------------ online ----

        test("things that need a provider are routed to the online intents") {
            assertEquals(IntentKind.WEB_SEARCH, best("what's the weather today"))
            assertEquals(IntentKind.WEB_SEARCH, best("latest news headlines"))
            assertEquals(IntentKind.ASK_ONLINE_AI, best("ask the online AI to summarise this"))
            assertEquals(IntentKind.ASK_ONLINE_AI, best("translate this to hindi"))
            assertEquals(IntentKind.OPEN_URL, best("open https://example.com/report"))
        }

        // ------------------------------------------------------- offline computation ----

        test("arithmetic and unit conversion are recognised") {
            assertEquals(IntentKind.CALCULATE, best("what's 20 percent of 4500"))
            assertEquals(IntentKind.CALCULATE, best("calculate 15 times 7"))
            assertEquals(IntentKind.CONVERT_UNITS, best("convert 100 usd to inr"))
            assertEquals(IntentKind.CONVERT_UNITS, best("how many km in 5 miles"))
        }

        // ---------------------------------------------------------------- dialogue ----

        test("short dialogue replies are classified on their own") {
            assertEquals(IntentKind.CONFIRM, best("yes"))
            assertEquals(IntentKind.CONFIRM, best("go ahead"))
            assertEquals(IntentKind.DENY, best("no"))
            assertEquals(IntentKind.CANCEL, best("cancel"))
            assertEquals(IntentKind.CANCEL, best("never mind"))
            assertEquals(IntentKind.REPEAT, best("say that again"))
            assertEquals(IntentKind.THANKS, best("thanks"))
            assertEquals(IntentKind.GREETING, best("hello"))
            assertEquals(IntentKind.GREETING, best("good morning"))
            assertEquals(IntentKind.CHITCHAT, best("tell me a joke"))
        }

        // --------------------------------------------------------------- wake word ----

        test("the wake word is detected and stripped") {
            val text = "hey jarvis open settings"
            assertTrue(IntentRules.isAddressedTo(text, config))
            assertEquals("open settings", IntentRules.stripAddress(text, config))
            assertEquals(IntentKind.OPEN_APP, best(text))
        }

        test("a custom nickname and wake word both work after renaming") {
            val renamed = config.copy(nickname = "Friday", wakeWord = "Friday")
            assertTrue(IntentRules.isAddressedTo("hey friday what time is it", renamed))
            assertEquals("what time is it", IntentRules.stripAddress("hey friday what time is it", renamed))
            assertEquals(IntentKind.DEVICE_STATUS, best("friday, what time is it", renamed))
        }

        test("a bare wake word strips to nothing") {
            assertEquals("", IntentRules.stripAddress("hey jarvis", config))
        }

        test("a greeting followed by a request is a request") {
            assertEquals(IntentKind.OPEN_APP, best("hey jarvis open the camera"))
            assertEquals(IntentKind.DEVICE_STATUS, best("hello what time is it"))
        }

        // ------------------------------------------------------- explainability ----

        test("every match carries the evidence for it") {
            val hits = IntentRules.match("remind me to call mom at 7pm", config)
            assertTrue(hits.isNotEmpty(), "expected at least one candidate")
            for (hit in hits) {
                assertTrue(hit.reason.startsWith("matched"), "reason was '${hit.reason}'")
                assertTrue(hit.confidence in 0.0..0.99, "confidence out of range: ${hit.confidence}")
            }
        }

        test("only the strongest hit per intent survives") {
            val hits = IntentRules.match("remind me to note that I prefer tea", config)
            val unique = hits.map { it.kind }.distinct()
            assertEquals(unique.size, hits.size, "duplicate kinds would fake agreement")
        }

        test("ambiguity is surfaced rather than resolved silently") {
            // "remind me to note that..." genuinely reads as both a reminder and a memory write.
            val hits = IntentRules.match("remind me to note that I prefer tea", config)
            assertTrue(hits.size >= 2, "expected competing candidates, got $hits")
            val top = hits[0]
            val second = hits[1]
            assertTrue(
                top.confidence - second.confidence <= IntentRules.AMBIGUITY_MARGIN,
                "expected a close call between ${top.kind} and ${second.kind}",
            )
        }

        // ---------------------------------------------------------- no invention ----

        test("sentences with nothing recognisable match no rule at all") {
            assertNull(best("banana banana banana"))
            assertNull(best(""))
            assertNull(best("   "))
            assertTrue(kinds("the quick brown fox").isEmpty())
        }

        test("a question about a verb is not a command using that verb") {
            assertTrue(best("what does the open button do") != IntentKind.OPEN_APP)
            assertTrue(best("which app is best for notes") != IntentKind.OPEN_APP)
        }

        test("ordinary sentences are not mistaken for times") {
            // Regression: a substring test for "at " used to match "wh-at can you do".
            assertTrue(kinds("what can you do").isNotEmpty())
            assertEquals(IntentKind.ASK_CAPABILITIES, best("what can you do"))
        }
    }
}
