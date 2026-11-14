package dev.jarvis.core.nlu

import dev.jarvis.core.config.AssistantConfig
import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertNotNull
import dev.jarvis.core.testing.assertNull
import dev.jarvis.core.testing.assertTrue

/**
 * Slot extraction.
 *
 * An intent without its arguments cannot be acted on, and a *wrong* argument is worse than a
 * missing one: a missing app name produces "which app?", while a guessed one opens something the
 * user never named. These tests cover both sides - what gets extracted, and what deliberately
 * does not.
 *
 * Text arrives here already normalised (lowercase, punctuation folded to spaces), which is how
 * [IntentRules] calls it.
 */
object EntitiesSuite : Suite("nlu/entities") {

    private val config = AssistantConfig.DEFAULT

    private fun one(text: String, kind: IntentKind, entity: EntityKind): Entity? =
        EntityExtractor.extract(text, kind, config).firstOrNull { it.kind == entity }

    private fun value(text: String, kind: IntentKind, entity: EntityKind): String? =
        one(text, kind, entity)?.value

    init {
        // ---------------------------------------------------------------- apps ----

        test("an app name is taken from the words after the verb") {
            assertEquals("whatsapp", value("open whatsapp", IntentKind.OPEN_APP, EntityKind.APP_NAME))
            assertEquals("camera", value("launch the camera", IntentKind.OPEN_APP, EntityKind.APP_NAME))
            assertEquals("settings", value("take me to settings", IntentKind.OPEN_APP, EntityKind.APP_NAME))
        }

        test("the word 'app' and polite fillers are not part of the name") {
            assertEquals("play store", value("open the play store app", IntentKind.OPEN_APP, EntityKind.APP_NAME))
            assertEquals("chrome", value("open chrome please", IntentKind.OPEN_APP, EntityKind.APP_NAME))
        }

        test("common aliases normalise to one spelling") {
            assertEquals("instagram", value("open insta", IntentKind.OPEN_APP, EntityKind.APP_NAME))
            assertEquals("youtube", value("open yt", IntentKind.OPEN_APP, EntityKind.APP_NAME))
            assertEquals("maps", value("open google maps", IntentKind.OPEN_APP, EntityKind.APP_NAME))
        }

        test("a quoted app name is taken verbatim") {
            val entity = assertNotNull(one("open 'my banking app'", IntentKind.OPEN_APP, EntityKind.APP_NAME))
            assertEquals("my banking app", entity.value)
        }

        test("no app name yields nothing rather than a guess") {
            assertNull(value("open", IntentKind.OPEN_APP, EntityKind.APP_NAME))
            assertNull(value("please open the", IntentKind.OPEN_APP, EntityKind.APP_NAME))
        }

        test("an unresolvable name is passed through for the port to reject") {
            // Extraction is lexical on purpose: only DevicePort can see installed apps, so a name
            // that matches nothing is reported rather than silently swapped for a plausible app.
            assertEquals("thing", value("please open the thing", IntentKind.OPEN_APP, EntityKind.APP_NAME))
        }

        // ---------------------------------------------------------------- urls ----

        test("a url is extracted and made absolute") {
            assertEquals(
                "https://example.com/report",
                value("open https://example.com/report", IntentKind.OPEN_URL, EntityKind.URL),
            )
            // The host is kept as spoken: "www" may be significant, so it is not stripped.
            assertEquals(
                "https://www.example.com",
                value("visit www.example.com", IntentKind.OPEN_URL, EntityKind.URL),
            )
        }

        // ------------------------------------------------------------ ordinals ----

        test("ordinals are one-based, matching how people count screens") {
            assertEquals("2", value("tap the second one", IntentKind.SELECT_ORDINAL, EntityKind.ORDINAL))
            assertEquals("3", value("the 3rd", IntentKind.SELECT_ORDINAL, EntityKind.ORDINAL))
            assertEquals("1", value("the first", IntentKind.SELECT_ORDINAL, EntityKind.ORDINAL))
        }

        // ------------------------------------------------- numbers and levels ----

        test("numbers ignore articles that would otherwise read as one") {
            // Text.wordToNumber("a") is 1, so "set a timer" must not produce NUMBER=1.
            assertNull(value("set a timer for 10 minutes", IntentKind.START_TIMER, EntityKind.NUMBER)
                ?.takeIf { it == "1" })
            assertEquals("10", value("set a timer for 10 minutes", IntentKind.START_TIMER, EntityKind.NUMBER))
            assertEquals("40", value("set volume to 40", IntentKind.SET_VOLUME, EntityKind.NUMBER))
        }

        test("percentages are read as percentages") {
            assertEquals("40", value("set volume to 40 percent", IntentKind.SET_VOLUME, EntityKind.PERCENT))
            assertEquals("40", value("set volume to 40 percent", IntentKind.SET_VOLUME, EntityKind.LEVEL))
        }

        test("a level outside 0-100 is not accepted") {
            assertNull(value("set volume to 4000", IntentKind.SET_VOLUME, EntityKind.LEVEL))
        }

        test("direction is the last directional word spoken") {
            assertEquals("down", value("scroll down", IntentKind.SCROLL, EntityKind.DIRECTION))
            assertEquals("forward", value("skip forward", IntentKind.MEDIA_SKIP, EntityKind.DIRECTION))
            assertEquals("right", value("swipe right", IntentKind.SCROLL, EntityKind.DIRECTION))
        }

        // ------------------------------------------------------------- settings ----

        test("settings are folded onto canonical keys") {
            assertEquals("torch", value("turn on the torch", IntentKind.CHANGE_SETTING, EntityKind.SETTING))
            assertEquals("wifi", value("enable wi-fi", IntentKind.CHANGE_SETTING, EntityKind.SETTING))
            assertEquals("dnd", value("turn on do not disturb", IntentKind.CHANGE_SETTING, EntityKind.SETTING))
            assertEquals("airplane_mode", value("turn on airplane mode", IntentKind.CHANGE_SETTING, EntityKind.SETTING))
            assertEquals("dark_mode", value("enable dark mode", IntentKind.CHANGE_SETTING, EntityKind.SETTING))
        }

        test("the requested setting value is on or off") {
            assertEquals("on", value("turn on the torch", IntentKind.CHANGE_SETTING, EntityKind.SETTING_VALUE))
            assertEquals("off", value("turn off bluetooth", IntentKind.CHANGE_SETTING, EntityKind.SETTING_VALUE))
            assertEquals("off", value("disable wifi", IntentKind.CHANGE_SETTING, EntityKind.SETTING_VALUE))
        }

        test("a setting request with no setting named yields no setting entity") {
            assertNull(value("turn it off", IntentKind.CHANGE_SETTING, EntityKind.SETTING))
        }

        // --------------------------------------------------------------- text ----

        test("quoted text is taken verbatim for typing and messages") {
            assertEquals(
                "hello there",
                value("type 'hello there'", IntentKind.TYPE_TEXT, EntityKind.MESSAGE_BODY),
            )
            assertEquals(
                "i'll be late",
                value("send a message saying i'll be late", IntentKind.SEND_MESSAGE, EntityKind.MESSAGE_BODY),
            )
        }

        test("an element description keeps the user's own wording") {
            assertEquals(
                "send button",
                value("tap the send button", IntentKind.TAP_ELEMENT, EntityKind.ELEMENT_DESCRIPTION),
            )
        }

        // ------------------------------------------------------------ people ----

        test("contacts are the spoken name, not a resolved number") {
            assertEquals("mom", value("call mom", IntentKind.MAKE_CALL, EntityKind.CONTACT))
            assertEquals("rahul", value("send a text to rahul", IntentKind.SEND_MESSAGE, EntityKind.CONTACT))
        }

        test("a reminder recipient is only taken when one is actually named") {
            assertEquals("mom", EntityExtractor.recipient("remind mom to call the doctor")?.value)
            // "remind me" has no third-party recipient.
            assertNull(EntityExtractor.recipient("remind me to call the doctor"))
        }

        test("non-names are not turned into contacts") {
            assertNull(value("call it a day", IntentKind.MAKE_CALL, EntityKind.CONTACT))
        }

        // ------------------------------------------------------------- topics ----

        test("a topic is what remains after the lead-in") {
            assertEquals("card number", value("forget my card number", IntentKind.FORGET_TOPIC, EntityKind.TOPIC))
            assertEquals("thesis", value("what do you remember about my thesis", IntentKind.QUERY_MEMORY, EntityKind.TOPIC))
        }

        test("a reminder keeps its task text") {
            assertEquals(
                "call the doctor",
                value("remind me to call the doctor", IntentKind.CREATE_REMINDER, EntityKind.QUERY),
            )
        }

        test("an explanation keeps the thing being explained") {
            assertEquals(
                "difference between ac and dc",
                value("explain the difference between ac and dc", IntentKind.EXPLAIN, EntityKind.QUERY),
            )
        }

        // ------------------------------------------------------------ assistant ----

        test("a new assistant name is extracted from several phrasings") {
            assertEquals("friday", value("from now on your name is friday", IntentKind.RENAME_ASSISTANT, EntityKind.ASSISTANT_NAME))
            assertEquals("boss", value("i'll call you boss", IntentKind.RENAME_ASSISTANT, EntityKind.ASSISTANT_NAME))
            assertEquals("atom", value("call yourself 'atom'", IntentKind.RENAME_ASSISTANT, EntityKind.ASSISTANT_NAME))
        }

        test("asking the assistant's name is not renaming it") {
            assertNull(value("what should i call you?", IntentKind.RENAME_ASSISTANT, EntityKind.ASSISTANT_NAME))
        }

        test("a name that is really leftover sentence structure is rejected") {
            assertNull(value("change your name to be more friendly", IntentKind.RENAME_ASSISTANT, EntityKind.ASSISTANT_NAME))
        }

        // ---------------------------------------------------------- preferences ----

        test("a preference is split into key and value") {
            assertEquals("theme", value("i prefer dark mode", IntentKind.SET_PREFERENCE, EntityKind.PREFERENCE_KEY))
            assertEquals("dark", value("i prefer dark mode", IntentKind.SET_PREFERENCE, EntityKind.PREFERENCE_VALUE))
            assertEquals("language", value("set my language to malayalam", IntentKind.SET_PREFERENCE, EntityKind.PREFERENCE_KEY))
            assertEquals("malayalam", value("set my language to malayalam", IntentKind.SET_PREFERENCE, EntityKind.PREFERENCE_VALUE))
        }

        test("a preference with no stated value leaves the value empty") {
            // The key is known but the value is not, so the caller must ask "set to what?"
            // rather than inventing one.
            assertEquals("coffee", value("i prefer coffee", IntentKind.SET_PREFERENCE, EntityKind.PREFERENCE_KEY))
            assertNull(value("i prefer coffee", IntentKind.SET_PREFERENCE, EntityKind.PREFERENCE_VALUE))
        }

        // ----------------------------------------------------------- documents ----

        test("documents are found by filename or by title") {
            assertEquals("report.pdf", value("import report.pdf", IntentKind.INGEST_KNOWLEDGE, EntityKind.DOCUMENT))
            assertEquals(
                "thesis notes",
                value("learn from the document called thesis notes", IntentKind.INGEST_KNOWLEDGE, EntityKind.DOCUMENT),
            )
        }

        // ---------------------------------------------------------- automation ----

        test("an automation is split into trigger and action") {
            val text = "whenever i plug in the charger then turn on wifi"
            val trigger = assertNotNull(one(text, IntentKind.CREATE_AUTOMATION, EntityKind.AUTOMATION_TRIGGER))
            assertEquals("i plug in the charger", trigger.value)
            val action = assertNotNull(one(text, IntentKind.CREATE_AUTOMATION, EntityKind.AUTOMATION_ACTION))
            assertEquals("turn on wifi", action.value)
        }

        // -------------------------------------------------------- computation ----

        test("arithmetic keeps the expression verbatim for the evaluator") {
            assertEquals(
                "20 percent of 4500",
                value("what's 20 percent of 4500", IntentKind.CALCULATE, EntityKind.QUERY),
            )
            assertEquals(
                "15 times 7",
                value("calculate 15 times 7", IntentKind.CALCULATE, EntityKind.QUERY),
            )
        }

        test("a conversion keeps amount, source unit and target unit") {
            assertEquals(
                "100 usd to inr",
                value("convert 100 usd to inr", IntentKind.CONVERT_UNITS, EntityKind.QUERY),
            )
        }

        // ------------------------------------------------------------- privacy ----

        test("a secret phrase is never turned into an entity") {
            // The phrase is a credential. Copying it into parse results would spread it into
            // confirmations, logs and memory, which is exactly what must not happen.
            val entities = EntityExtractor.extract(
                "my secret phrase is open sesame",
                IntentKind.AUTHENTICATE,
                config,
            )
            assertTrue(entities.isEmpty(), "authentication must extract nothing, got $entities")
        }

        test("dialogue intents carry no entities") {
            assertTrue(EntityExtractor.extract("yes", IntentKind.CONFIRM, config).isEmpty())
            assertTrue(EntityExtractor.extract("thanks", IntentKind.THANKS, config).isEmpty())
        }
    }
}
