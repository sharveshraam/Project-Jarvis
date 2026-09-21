package dev.jarvis.core.config

import dev.jarvis.core.model.DifficultyLevel
import dev.jarvis.core.model.LearningStyle
import dev.jarvis.core.store.InMemoryConfigStore
import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertContains
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertFalse
import dev.jarvis.core.testing.assertNotNull
import dev.jarvis.core.testing.assertTrue

/**
 * Configuration, and in particular the requirement that the assistant's name is entirely the
 * user's choice and can be changed at any time.
 */
object ConfigSuite : Suite("config") {
    init {
        test("defaults are local-first and private by default") {
            val config = AssistantConfig.DEFAULT
            assertEquals("Jarvis", config.nickname)
            assertFalse(config.onlineAiEnabled, "online AI must be off by default")
            assertFalse(config.onlineSearchEnabled, "online search must be off by default")
            assertFalse(config.voiceHistoryEnabled, "voice transcripts must be opt-in")
            assertTrue(config.memoryEnabled)
            assertTrue(config.confirmConsequentialActions, "consequential actions must ask first")
            assertFalse(config.wakeWordEnabled, "an always-open microphone must be opt-in")
        }

        test("the nickname can be anything the user likes") {
            val base = AssistantConfig.DEFAULT
            listOf(
                "Friday", "J.A.R.V.I.S.", "buddy", "சகா", "助手", "R2", "ok google's rival",
                "a very long but legal name", "🤖",
            ).forEach { candidate ->
                val renamed = assertNotNull(base.renamedTo(candidate), "rejected: $candidate")
                assertEquals(candidate.trim(), renamed.nickname)
            }
        }

        test("renaming does not disturb anything else") {
            val before = AssistantConfig.DEFAULT.copy(
                userName = "Sharvesh",
                memoryEnabled = false,
                responseStyle = ResponseStyle.TECHNICAL,
            )
            val after = assertNotNull(before.renamedTo("Friday"))
            assertEquals("Friday", after.nickname)
            assertEquals("Sharvesh", after.userName)
            assertFalse(after.memoryEnabled)
            assertEquals(ResponseStyle.TECHNICAL, after.responseStyle)
        }

        test("renaming also updates the wake word so it keeps working") {
            val renamed = assertNotNull(AssistantConfig.DEFAULT.renamedTo("Friday"))
            assertEquals("Friday", renamed.wakeWord, "the wake word must follow the nickname")
        }

        test("a name change is reversible and repeatable") {
            var config = AssistantConfig.DEFAULT
            config = assertNotNull(config.renamedTo("Friday"))
            config = assertNotNull(config.renamedTo("Alfred"))
            config = assertNotNull(config.renamedTo("Jarvis"))
            assertEquals("Jarvis", config.nickname)
        }

        test("unusable names are rejected with a reason") {
            val config = AssistantConfig.DEFAULT
            assertTrue(config.validateNickname("") is NicknameValidation.Rejected)
            assertTrue(config.validateNickname("   ") is NicknameValidation.Rejected)
            assertEquals(null, config.renamedTo(""))
            assertEquals(null, config.renamedTo("line\nbreak"))
            assertEquals(null, config.renamedTo("tab\there"))

            val tooLong = "x".repeat(AssistantConfig.MAX_NICKNAME_LENGTH + 1)
            val rejection = config.validateNickname(tooLong)
            assertTrue(rejection is NicknameValidation.Rejected)
            assertContains((rejection as NicknameValidation.Rejected).reason, "limit")
        }

        test("an unpronounceable name is accepted with a warning, not refused") {
            val config = AssistantConfig.DEFAULT
            // The name is the user's choice. "..." and "🤖" display fine, so they are allowed -
            // but the consequence for the spoken wake word is stated rather than hidden.
            listOf("...", "🤖", "!!!").forEach { candidate ->
                val result = config.validateNickname(candidate)
                assertTrue(result is NicknameValidation.Accepted, "$candidate was refused")
                val accepted = result as NicknameValidation.Accepted
                assertNotNull(accepted.warning, "$candidate should carry a warning")
                assertContains(accepted.warning!!, "wake word")
                assertEquals(candidate, config.renamedTo(candidate)?.nickname)
            }
            // A normal name carries no warning.
            val plain = config.validateNickname("Friday") as NicknameValidation.Accepted
            assertEquals(null, plain.warning)
        }

        test("surrounding whitespace is trimmed rather than rejected") {
            val accepted = AssistantConfig.DEFAULT.validateNickname("  Friday  ")
            assertTrue(accepted is NicknameValidation.Accepted)
            assertEquals("Friday", (accepted as NicknameValidation.Accepted).name)
        }

        test("config round-trips through json") {
            val original = AssistantConfig.DEFAULT.copy(
                nickname = "Friday",
                userName = "Sharvesh",
                responseStyle = ResponseStyle.STEP_BY_STEP,
                learningStyle = LearningStyle.SOCRATIC,
                defaultTeachingLevel = DifficultyLevel.ADVANCED,
                memoryEnabled = false,
                onlineAiEnabled = true,
                voiceHistoryEnabled = true,
                wakeWordEnabled = true,
                wakeWord = "friday",
                ttsPitch = 0.8f,
                ttsRate = 1.25f,
                daypartHours = mapOf("dinner" to 21, "morning" to 6),
                maxAgentSteps = 20,
                onlineProviderId = "provider-x",
                localModelId = "model-y",
            )
            val restored = AssistantConfig.fromJson(original.toJson().encode())
            assertEquals(original, restored)
        }

        test("unknown and missing keys fall back to defaults") {
            val restored = AssistantConfig.fromJson(
                """{"nickname":"Friday","responseStyle":"A_STYLE_FROM_THE_FUTURE",
                   |"somethingNew":123,"maxAgentSteps":9999}""".trimMargin(),
            )
            assertEquals("Friday", restored.nickname)
            assertEquals(ResponseStyle.CONCISE, restored.responseStyle, "unknown enum falls back")
            assertEquals(50, restored.maxAgentSteps, "out-of-range values are clamped")
        }

        test("corrupt config cannot lock the user out") {
            assertEquals(AssistantConfig.DEFAULT, AssistantConfig.fromJson(null))
            assertEquals(AssistantConfig.DEFAULT, AssistantConfig.fromJson(""))
            assertEquals(AssistantConfig.DEFAULT, AssistantConfig.fromJson("{not json"))
            assertEquals(AssistantConfig.DEFAULT, AssistantConfig.fromJson("[1,2,3]"))
            // A blank nickname in stored config must not produce a nameless assistant.
            assertEquals("Jarvis", AssistantConfig.fromJson("{\"nickname\":\"  \"}").nickname)
        }

        test("daypart lookup is case and space tolerant") {
            val config = AssistantConfig.DEFAULT
            assertEquals(20, config.daypartHour("Dinner"))
            assertEquals(20, config.daypartHour("  dinner "))
            assertEquals(null, config.daypartHour("supper"))
        }

        test("the config store starts uninitialised and persists what it is given") {
            val store = InMemoryConfigStore()
            assertTrue(store.isUninitialized(), "a fresh install has no saved config")
            assertEquals(AssistantConfig.DEFAULT, store.load())

            store.save(AssistantConfig.DEFAULT.copy(nickname = "Friday"))
            assertFalse(store.isUninitialized())
            assertEquals("Friday", store.load().nickname)

            store.save(store.load().copy(memoryEnabled = false))
            assertFalse(store.load().memoryEnabled)
            assertEquals("Friday", store.load().nickname, "unrelated settings must survive a save")
        }

        test("response style labels are user-presentable") {
            ResponseStyle.entries.forEach { assertTrue(it.label.isNotBlank()) }
            LearningStyle.entries.forEach { assertTrue(it.label.isNotBlank()) }
        }
    }
}
