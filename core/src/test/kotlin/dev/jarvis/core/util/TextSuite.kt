package dev.jarvis.core.util

import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertContains
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertFalse
import dev.jarvis.core.testing.assertNotNull
import dev.jarvis.core.testing.assertTrue

object TextSuite : Suite("util/text") {
    init {
        test("normalize strips punctuation, case and extra whitespace") {
            assertEquals("open youtube now", Text.normalize("  Open,  YouTube!!  NOW  "))
            // Apostrophes are preserved on purpose: contractions are meaningful tokens.
            assertEquals("it's 6 pm", Text.normalize("It's 6 PM."))
        }

        test("normalize folds smart quotes and dashes") {
            assertEquals(
                "i'll be there maybe",
                Text.normalize("I\u2019ll be there \u2014 maybe"),
            )
            assertEquals("state of the art", Text.normalize("state-of-the-art"))
        }

        test("keywords drops stop words but keeps content") {
            val keywords = Text.keywords("Please could you open the YouTube app for me")
            assertTrue(keywords.contains("open"))
            assertTrue(keywords.contains("youtube"))
            assertTrue(keywords.contains("app"))
            assertFalse(keywords.contains("please"))
            assertFalse(keywords.contains("the"))
        }

        test("keywords falls back to all tokens when everything is a stop word") {
            val keywords = Text.keywords("the a of")
            assertEquals(listOf("the", "a", "of"), keywords)
        }

        test("stem collapses inflections used by memory matching") {
            assertEquals(Text.stem("message"), Text.stem("messages"))
            assertEquals(Text.stem("remind"), Text.stem("reminding"))
            assertEquals("study", Text.stem("studies"))
            assertEquals("run", Text.stem("running"))
            assertEquals("stop", Text.stem("stopped"))
            assertEquals("box", Text.stem("boxes"))
            assertEquals("video", Text.stem("videos"))
            assertEquals("app", Text.stem("apps"))
        }

        test("stem does not butcher words whose trailing s is real") {
            assertEquals("news", Text.stem("news"))
            assertEquals("electronics", Text.stem("electronics"))
            assertEquals("robotics", Text.stem("robotics"))
            assertEquals("physics", Text.stem("physics"))
            assertEquals("bus", Text.stem("bus"))
            assertEquals("sms", Text.stem("sms"))
        }

        test("stem still strips ordinary plurals that look similar") {
            assertEquals("map", Text.stem("maps"))
            assertEquals("photo", Text.stem("photos"))
            assertEquals("class", Text.stem("classes"))
            assertEquals("watch", Text.stem("watches"))
        }

        test("levenshtein computes edit distance") {
            assertEquals(0, Text.levenshtein("kitten", "kitten"))
            assertEquals(3, Text.levenshtein("kitten", "sitting"))
            assertEquals(4, Text.levenshtein("", "abcd"))
        }

        test("stringSimilarity is tolerant of typos") {
            assertTrue(Text.stringSimilarity("youtube", "youtub") > 0.8)
            assertTrue(Text.stringSimilarity("whatsapp", "whtasapp") > 0.7)
            assertTrue(Text.stringSimilarity("youtube", "spotify") < 0.6)
        }

        test("phraseSimilarity ignores word order") {
            val score = Text.phraseSimilarity("youtube music", "music youtube")
            assertTrue(score > 0.9, "score was $score")
        }

        test("phraseSimilarity prefers the more complete candidate over a subset") {
            val exact = Text.phraseSimilarity("youtube musc", "YouTube Music")
            val subset = Text.phraseSimilarity("youtube musc", "YouTube")
            assertTrue(exact > subset, "exact=$exact subset=$subset")
        }

        test("phraseSimilarity tolerates a typo in a multi-word query") {
            val score = Text.phraseSimilarity("youtube musc", "YouTube Music")
            assertTrue(score > 0.55, "score was $score")
        }

        test("phraseSimilarity separates unrelated phrases") {
            val score = Text.phraseSimilarity("open youtube", "delete all memories")
            assertTrue(score < 0.3, "score was $score")
        }

        test("fuzzyContains finds a misspelled single word") {
            assertTrue(Text.fuzzyContains("Open YouTube and play", "youtub"))
            assertFalse(Text.fuzzyContains("Open Spotify and play", "youtube"))
        }

        test("fuzzyContains finds a multi-word needle in a long sentence") {
            val haystack = "Top news: the second recommended video is about mesh analysis"
            assertTrue(Text.fuzzyContains(haystack, "mesh analysis"))
            assertTrue(Text.fuzzyContains(haystack, "second recommended video"))
        }

        test("bestMatch picks the closest app name") {
            val apps = listOf("YouTube", "YouTube Music", "Google Maps", "WhatsApp")
            val match = assertNotNull(Text.bestMatch("youtube musc", apps))
            assertEquals("YouTube Music", match.first)
        }

        test("bestMatch returns null below threshold") {
            val apps = listOf("YouTube", "WhatsApp")
            assertTrue(Text.bestMatch("telegram", apps, threshold = 0.6) == null)
        }

        test("ordinalToIndex understands spoken and written ordinals") {
            assertEquals(1, Text.ordinalToIndex("first"))
            assertEquals(2, Text.ordinalToIndex("second"))
            assertEquals(2, Text.ordinalToIndex("2nd"))
            assertEquals(3, Text.ordinalToIndex("third"))
            assertEquals(7, Text.ordinalToIndex("7"))
        }

        test("wordToNumber understands spoken quantities") {
            assertEquals(5, Text.wordToNumber("five"))
            assertEquals(40, Text.wordToNumber("forty"))
            assertEquals(12, Text.wordToNumber("dozen"))
            assertEquals(9, Text.wordToNumber("9"))
        }

        test("firstQuotedSpan extracts dictated message bodies") {
            val span = assertNotNull(
                Text.firstQuotedSpan("send John a message saying \"I'll reach there at 6\""),
            )
            assertEquals("I'll reach there at 6", span)
        }

        test("firstQuotedSpan returns null when nothing is quoted") {
            assertTrue(Text.firstQuotedSpan("send John a message about dinner") == null)
        }

        test("truncate keeps output within the limit") {
            val long = "the quick brown fox jumps over the lazy dog"
            val truncated = Text.truncate(long, 20)
            assertEquals(20, truncated.length)
            assertContains(truncated, "the quick")
        }
    }
}
