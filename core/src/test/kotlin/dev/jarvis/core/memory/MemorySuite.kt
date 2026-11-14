package dev.jarvis.core.memory

import dev.jarvis.core.model.MemoryCategory
import dev.jarvis.core.model.MemorySource
import dev.jarvis.core.store.InMemoryMemoryStore
import dev.jarvis.core.store.InMemoryVectorStore
import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertContains
import dev.jarvis.core.testing.assertDoesNotContain
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertFalse
import dev.jarvis.core.testing.assertNotNull
import dev.jarvis.core.testing.assertTrue
import dev.jarvis.core.util.FixedClock
import dev.jarvis.core.util.SequentialIds

/**
 * Memory behaviour, tested against the exact scenarios the requirements name.
 *
 * Runs entirely offline on in-memory stores, which is the point: memory is the feature that
 * most needs to be provably local.
 */
object MemorySuite : Suite("memory") {

    private const val START = 1_700_000_000_000L
    private const val HOUR = 3_600_000L
    private const val DAY = 24 * HOUR

    private class Harness(memoryEnabled: Boolean = true) {
        val clock = FixedClock(START)
        val ids = SequentialIds()
        val store = InMemoryMemoryStore()
        val vectors = InMemoryVectorStore()
        var enabled = memoryEnabled
        val manager = MemoryManager(
            store = store,
            vectors = vectors,
            clock = clock,
            ids = ids,
            config = { enabled },
        )
    }

    init {
        test("an explicit 'remember that...' is stored as long-term memory") {
            val h = Harness()
            val record = assertNotNull(
                h.manager.remember("Remember that I'm working on an ECE robotics project."),
            )
            assertEquals(MemoryCategory.LONG_TERM, record.category)
            assertTrue(record.permanent, "an explicit fact must not be temporary")
            assertEquals(MemorySource.USER_STATED, record.source)
            // The topic describes the subject, not the instruction.
            assertNotNull(record.topic)
            assertContains(record.topic!!, "robotics")
            assertDoesNotContain(record.topic!!, "remember")
            assertEquals(1, h.manager.count)
        }

        test("a later request about the same subject recalls it") {
            val h = Harness()
            h.manager.remember("Remember that I'm working on an ECE robotics project.")
            val hits = h.manager.recall("Help me with my robotics project")
            assertTrue(hits.isNotEmpty(), "expected the robotics memory to be recalled")
            assertContains(hits.first().record.text, "robotics")
        }

        test("recall survives paraphrase, word order and plurals") {
            val h = Harness()
            h.manager.remember("My favourite editor is VS Code and I use it for embedded work.")
            assertTrue(h.manager.recall("which editor do i like").isNotEmpty())
            assertTrue(h.manager.recall("embedded work editor").isNotEmpty())
            h.manager.remember("I have three assignments due this week for electronics.")
            assertTrue(h.manager.recall("electronics assignment").isNotEmpty())
        }

        test("an unrelated query recalls nothing, not the highest-ranked memory") {
            val h = Harness()
            h.manager.remember(
                "Remember that I'm working on an ECE robotics project.",
                pinned = true,
                importance = 1.0,
            )
            // Boosts must refine ranking, never manufacture relevance.
            val hits = h.manager.recall("what is the airspeed velocity of a swallow")
            assertEquals(0, hits.size, "irrelevant query returned ${hits.map { it.record.text }}")
        }

        test("'what do you remember about me?' lists profile, preferences and facts") {
            val h = Harness()
            h.manager.remember("My name is Sharvesh.", MemoryCategory.USER_PROFILE)
            h.manager.remember("I prefer short answers.", MemoryCategory.PREFERENCE)
            h.manager.remember("Remember that I'm working on an ECE robotics project.")
            val summary = h.manager.profileSummary("Jarvis")
            assertContains(summary, "Sharvesh")
            assertContains(summary, "short answers")
            assertContains(summary, "robotics")
            assertContains(summary, "USER PROFILE")
            assertContains(summary, "stored locally")
        }

        test("'what do you remember about me?' is honest when empty") {
            val h = Harness()
            val summary = h.manager.profileSummary("Jarvis")
            assertContains(summary, "Nothing yet")
            assertDoesNotContain(summary, "USER PROFILE")
        }

        test("'forget what I told you about X' removes only that topic") {
            val h = Harness()
            h.manager.remember("Remember that I'm working on an ECE robotics project.")
            h.manager.remember("My name is Sharvesh.", MemoryCategory.USER_PROFILE)
            h.manager.remember("I like filter coffee.", MemoryCategory.PREFERENCE)

            val result = h.manager.forgetMatching("forget what I told you about the robotics project")
            assertEquals(1, result.deleted, "deleted: ${result.deletedTexts}")
            assertContains(result.deletedTexts.first(), "robotics")

            assertEquals(2, h.manager.count)
            assertTrue(h.manager.all().any { it.text.contains("Sharvesh") })
            assertTrue(h.manager.all().any { it.text.contains("filter coffee") })
        }

        test("forgetting something never stored reports that honestly") {
            val h = Harness()
            h.manager.remember("I like filter coffee.", MemoryCategory.PREFERENCE)
            val result = h.manager.forgetMatching("forget my scuba diving certification")
            assertEquals(0, result.deleted)
            assertTrue(result.nothingDeleted)
            assertEquals(1, h.manager.count, "an unmatched forget must not delete anything")
        }

        test("'delete all my memories' clears everything including the indexes") {
            val h = Harness()
            h.manager.remember("Fact one about the robotics project.")
            h.manager.remember("Fact two about coffee.")
            h.manager.remember("Context.", MemoryCategory.CONVERSATION_CONTEXT)
            assertEquals(3, h.manager.count)

            val cleared = h.manager.forgetAll()
            assertEquals(3, cleared)
            assertEquals(0, h.manager.count)
            assertEquals(0, h.store.count())
            assertEquals(0, h.vectors.size)
            assertEquals(0, h.manager.recall("robotics project").size)
            assertContains(h.manager.profileSummary(), "Nothing yet")
        }

        test("near-duplicate saves merge instead of accumulating") {
            val h = Harness()
            val first = assertNotNull(h.manager.remember("My college is in Alappuzha."))
            val second = assertNotNull(h.manager.remember("my college is in alappuzha"))
            assertEquals(1, h.manager.count, "the same fact twice must not create two memories")
            assertEquals(first.id, second.id)
            assertTrue(second.importance >= first.importance, "repeating a fact raises its importance")
        }

        test("genuinely different facts are both kept") {
            val h = Harness()
            h.manager.remember("My college is in Alappuzha.")
            h.manager.remember("My hometown is Chennai.")
            assertEquals(2, h.manager.count)
        }

        test("temporary context expires; permanent memory does not") {
            val h = Harness()
            h.manager.remember(
                "The user is currently on the YouTube search results screen.",
                MemoryCategory.CONVERSATION_CONTEXT,
            )
            h.manager.remember("Remember that I'm working on an ECE robotics project.")

            h.clock.advance(2 * HOUR)
            assertEquals(0, h.manager.pruneExpired(h.clock.nowMillis()), "nothing should expire yet")
            assertEquals(2, h.manager.count)

            h.clock.advance(12 * HOUR)
            val pruned = h.manager.pruneExpired(h.clock.nowMillis())
            assertEquals(1, pruned, "only the temporary context should expire")
            assertEquals(1, h.manager.count)
            assertTrue(h.manager.all().single().text.contains("robotics"))
        }

        test("expired temporary context is excluded from recall before pruning") {
            val h = Harness()
            h.manager.remember(
                "The current screen shows a mesh analysis worksheet.",
                MemoryCategory.CONVERSATION_CONTEXT,
            )
            assertTrue(h.manager.recall("mesh analysis worksheet").isNotEmpty())
            h.clock.advance(12 * HOUR)
            assertEquals(
                0,
                h.manager.recall("mesh analysis worksheet", nowMillis = h.clock.nowMillis()).size,
            )
            // Explicitly asking for expired context still works, which is what a "what did we
            // talk about yesterday" answer needs.
            assertTrue(
                h.manager.recall(
                    "mesh analysis worksheet",
                    nowMillis = h.clock.nowMillis(),
                    includeExpired = true,
                ).isNotEmpty(),
            )
        }

        test("pinned temporary context survives pruning") {
            val h = Harness()
            h.manager.remember(
                "The user asked to keep this context for the whole session.",
                MemoryCategory.CONVERSATION_CONTEXT,
                pinned = true,
            )
            h.clock.advance(3 * DAY)
            assertEquals(0, h.manager.pruneExpired(h.clock.nowMillis()))
            assertEquals(1, h.manager.count)
        }

        test("disabling memory stops both writing and recall") {
            val h = Harness()
            h.manager.remember("I like filter coffee.", MemoryCategory.PREFERENCE)
            h.enabled = false
            assertEquals(null, h.manager.remember("Another secret."), "writes must be refused")
            assertEquals(0, h.manager.recall("filter coffee").size, "recall must be refused")
            assertContains(h.manager.profileSummary("Jarvis"), "turned off")
            // Re-enabling restores access to what was already stored.
            h.enabled = true
            assertTrue(h.manager.recall("filter coffee").isNotEmpty())
        }

        test("recall can be restricted to categories") {
            val h = Harness()
            h.manager.remember("My name is Sharvesh.", MemoryCategory.USER_PROFILE)
            h.manager.remember("Sharvesh is also the name of a robotics project.", MemoryCategory.LONG_TERM)
            val profileOnly = h.manager.recall(
                "sharvesh",
                categories = setOf(MemoryCategory.USER_PROFILE),
            )
            assertTrue(profileOnly.isNotEmpty())
            assertTrue(profileOnly.all { it.record.category == MemoryCategory.USER_PROFILE })
        }

        test("profile facts outrank throwaway context for the same query") {
            val h = Harness()
            h.manager.remember("Context about coffee on the current screen.", MemoryCategory.CONVERSATION_CONTEXT)
            h.manager.remember("I drink filter coffee every morning.", MemoryCategory.PREFERENCE, pinned = true)
            val hits = h.manager.recall("coffee", limit = 2)
            assertEquals(2, hits.size)
            assertEquals(MemoryCategory.PREFERENCE, hits.first().record.category)
        }

        test("markRecalled increases recall count and records the time") {
            val h = Harness()
            val record = assertNotNull(h.manager.remember("I like filter coffee."))
            assertEquals(0, record.recallCount)
            h.manager.recall("filter coffee").also { hits ->
                h.manager.markRecalled(hits.map { it.record.id })
            }
            val updated = assertNotNull(h.store.findById(record.id))
            assertEquals(1, updated.recallCount)
            assertNotNull(updated.lastRecalledAt)
        }

        test("contextBlock is attributed and length capped") {
            val h = Harness()
            h.manager.remember("I'm working on an ECE robotics project.")
            h.manager.remember("My favourite editor is VS Code.", MemoryCategory.PREFERENCE)
            val (block, usedIds) = h.manager.contextBlock("robotics project editor", limit = 5)
            assertContains(block, "robotics")
            assertContains(block, "[")
            assertTrue(block.startsWith("- "), "block should be a bulleted list: $block")
            assertTrue(usedIds.isNotEmpty())
            assertTrue(usedIds.size <= block.lines().size)
        }

        test("export and import round-trips memories") {
            val source = Harness()
            source.manager.remember("I'm working on an ECE robotics project.")
            source.manager.remember("My name is Sharvesh.", MemoryCategory.USER_PROFILE, pinned = true)
            source.manager.remember("I like filter coffee.", MemoryCategory.PREFERENCE, tags = listOf("food"))

            val exported = source.manager.exportJson()
            val target = Harness()
            val imported = target.manager.importJson(exported)
            assertEquals(3, imported)

            val pin = target.manager.all().first { it.text.contains("Sharvesh") }
            assertEquals(MemoryCategory.USER_PROFILE, pin.category)
            assertTrue(pin.pinned, "pinned state must survive export")
            val coffee = target.manager.all().first { it.text.contains("coffee") }
            assertTrue(coffee.tags.contains("food"), "tags must survive export")
            assertTrue(target.manager.recall("robotics").isNotEmpty(), "imported memories must be searchable")
        }

        test("import regenerates ids so a double import cannot overwrite") {
            val source = Harness()
            source.manager.remember("A fact worth keeping.")
            val exported = source.manager.exportJson()
            val target = Harness()
            target.manager.importJson(exported)
            target.manager.importJson(exported)
            assertEquals(2, target.manager.count)
            val ids = target.manager.all().map { it.id }.toSet()
            assertEquals(2, ids.size, "ids must be distinct")
        }

        test("import rejects malformed json without throwing") {
            val h = Harness()
            assertEquals(0, h.manager.importJson("not json at all"))
            assertEquals(0, h.manager.importJson("{\"not\":\"an array\"}"))
            assertEquals(0, h.manager.importJson(""))
            assertEquals(0, h.manager.count)
        }

        test("blank and whitespace-only memories are refused") {
            val h = Harness()
            assertEquals(null, h.manager.remember(""))
            assertEquals(null, h.manager.remember("   "))
            assertEquals(0, h.manager.count)
        }

        test("forgetting a single memory by id leaves the rest intact") {
            val h = Harness()
            val keep = assertNotNull(h.manager.remember("Keep this one."))
            val drop = assertNotNull(h.manager.remember("Drop this one."))
            assertTrue(h.manager.forget(drop.id))
            assertEquals(1, h.manager.count)
            assertNotNull(h.store.findById(keep.id))
            assertFalse(h.manager.forget("mem-does-not-exist"))
        }

        test("category deletion removes only that category") {
            val h = Harness()
            h.manager.remember("Task one.", MemoryCategory.TASK)
            h.manager.remember("Task two.", MemoryCategory.TASK)
            h.manager.remember("A long-term fact.", MemoryCategory.LONG_TERM)
            assertEquals(2, h.manager.forgetCategory(MemoryCategory.TASK))
            assertEquals(1, h.manager.count)
            assertEquals(MemoryCategory.LONG_TERM, h.manager.all().single().category)
        }
    }
}

object TopicKeySuite : Suite("memory/topic-key") {
    init {
        test("strips memory-command prefixes") {
            assertEquals(
                "i'm working on an ece robotics project",
                TopicKey.stripCommandPrefix("Remember that I'm working on an ECE robotics project"),
            )
            assertEquals(
                "buy milk tomorrow",
                TopicKey.stripCommandPrefix("Please remember to buy milk tomorrow"),
            )
            assertEquals(
                "the meeting moved to friday",
                TopicKey.stripCommandPrefix("Note that the meeting moved to Friday"),
            )
        }

        test("a key without a command prefix is left alone") {
            assertEquals("my robotics project", TopicKey.stripCommandPrefix("My robotics project"))
        }

        test("keys drop meta words and stems") {
            val key = TopicKey.of("Remember that I'm working on an ECE robotics project")
            assertContains(key, "robotics")
            assertContains(key, "project")
            assertDoesNotContain(key, "remember")
            assertDoesNotContain(key, "i'm")
        }

        test("display keeps the user's own words") {
            val display = TopicKey.display("Remember that I'm working on an ECE robotics project")
            assertContains(display, "ECE")
            assertDoesNotContain(display, "remember")
        }

        test("matchScore accepts a shortened query") {
            val stored = TopicKey.of("Remember that I'm working on an ECE robotics project")
            assertTrue(TopicKey.matches("the robotics project", stored), "stored='$stored'")
            assertTrue(TopicKey.matches("robotics", stored), "stored='$stored'")
        }

        test("matchScore rejects an unrelated query") {
            val stored = TopicKey.of("Remember that I'm working on an ECE robotics project")
            assertFalse(TopicKey.matches("my scuba diving trip", stored))
            assertFalse(TopicKey.matches("", stored))
            assertFalse(TopicKey.matches("coffee", ""))
        }

        test("keys are stable and bounded") {
            val long = "Remember " + List(30) { "topic$it" }.joinToString(" ")
            assertTrue(TopicKey.of(long).split(" ").size <= 6)
            assertEquals(TopicKey.of("I like coffee"), TopicKey.of("i like coffee"))
        }

        test("deletion requests are reduced to their subject") {
            assertEquals(
                "robotics project",
                TopicKey.stripForgetPrefix("Forget what I told you about the robotics project"),
            )
            assertEquals(
                "robotics project",
                TopicKey.stripForgetPrefix("please forget everything you know about the robotics project"),
            )
            assertEquals("coffee", TopicKey.stripForgetPrefix("forget about coffee"))
            assertEquals(
                "scuba trip",
                TopicKey.stripForgetPrefix("delete what you remember about my scuba trip from your memory"),
            )
        }

        test("a deletion request with no subject is not turned into an empty string") {
            // "Forget it" has no subject; returning the whole phrase is safer than returning
            // "", which would make forgetMatching fall back to matching everything.
            assertTrue(TopicKey.stripForgetPrefix("forget it").isNotBlank())
            assertTrue(TopicKey.stripForgetPrefix("forget").isNotBlank())
        }

        test("non-deletion text is left intact by stripForgetPrefix") {
            assertEquals(
                "my robotics project deadline",
                TopicKey.stripForgetPrefix("My robotics project deadline"),
            )
        }
    }
}
