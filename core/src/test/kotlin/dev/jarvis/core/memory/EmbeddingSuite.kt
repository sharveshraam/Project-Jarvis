package dev.jarvis.core.memory

import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertFalse
import dev.jarvis.core.testing.assertTrue

/**
 * Retrieval primitives.
 *
 * These are the two signals the hybrid retriever combines, so their behaviour is tested
 * directly rather than only through MemoryManager: when recall misbehaves, these tests say
 * whether the problem is the signal or the combination.
 */
object EmbeddingSuite : Suite("memory/embedding") {
    init {
        test("embeddings are deterministic and correctly dimensioned") {
            val embedder = HashingEmbedder()
            val a = embedder.embed("working on an ECE robotics project")
            val b = embedder.embed("working on an ECE robotics project")
            assertEquals(embedder.dimensions, a.size)
            assertTrue(a.contentEquals(b), "the same text must embed identically")
        }

        test("embeddings are L2 normalised") {
            val vector = HashingEmbedder().embed("the quick brown fox jumps")
            var sum = 0.0
            for (value in vector) sum += value.toDouble() * value
            assertTrue(kotlin.math.abs(sum - 1.0) < 1e-4, "norm was $sum")
        }

        test("identical text scores 1.0 and unrelated text scores near zero") {
            val embedder = HashingEmbedder()
            val text = "mesh analysis of a two loop circuit"
            val self = cosineSimilarity(embedder.embed(text), embedder.embed(text))
            assertTrue(kotlin.math.abs(self - 1.0) < 1e-6, "self-similarity was $self")
            val unrelated = embedder.embed("banana smoothie recipe for breakfast")
            assertTrue(cosineSimilarity(embedder.embed(text), unrelated) < 0.2)
        }

        test("shared vocabulary beats disjoint vocabulary") {
            val embedder = HashingEmbedder()
            val query = embedder.embed("robotics project deadlines")
            val close = embedder.embed("the robotics project has deadlines this week")
            val far = embedder.embed("a recipe for banana smoothie")
            assertTrue(
                cosineSimilarity(query, close) > cosineSimilarity(query, far),
                "close=${cosineSimilarity(query, close)} far=${cosineSimilarity(query, far)}",
            )
        }

        test("stemming makes singular and plural match") {
            val embedder = HashingEmbedder()
            val score = cosineSimilarity(
                embedder.embed("three electronics assignments"),
                embedder.embed("one electronics assignment"),
            )
            assertTrue(score > 0.5, "score was $score")
        }

        test("empty and stop-word-only input still produce a usable vector") {
            val embedder = HashingEmbedder()
            val empty = embedder.embed("")
            assertEquals(embedder.dimensions, empty.size)
            val stopWords = embedder.embed("the a of and")
            var nonZero = 0
            for (value in stopWords) if (value != 0f) nonZero++
            assertTrue(nonZero > 0, "a stop-word query must not collapse to a zero vector")
        }

        test("fnv1a is stable and well distributed") {
            // Stability matters more than distribution: vectors are persisted, so a hash change
            // would silently invalidate a stored index after an upgrade.
            assertEquals(HashingEmbedder.fnv1a("robotics"), HashingEmbedder.fnv1a("robotics"))
            assertFalse(HashingEmbedder.fnv1a("robotics") == HashingEmbedder.fnv1a("robotic"))
            val buckets = HashSet<Int>()
            for (i in 0 until 500) buckets.add(Math.floorMod(HashingEmbedder.fnv1a("term$i"), 384))
            assertTrue(buckets.size > 150, "hashing collapsed to ${buckets.size} distinct buckets")
        }

        test("the embedder describes itself honestly") {
            val description = HashingEmbedder().description
            assertTrue(description.contains("lexical"), "description was: $description")
            assertTrue(description.contains("No neural model"), "description was: $description")
        }
    }
}

object Bm25Suite : Suite("memory/bm25") {
    private fun index(vararg docs: Pair<String, String>): Bm25Index =
        Bm25Index().also { index -> docs.forEach { (id, text) -> index.add(id, text) } }

    init {
        test("exact term matches rank first") {
            val bm25 = index(
                "a" to "introduction to mesh analysis in circuits",
                "b" to "banana smoothie recipe",
                "c" to "thevenin theorem and equivalent circuits",
            )
            val results = bm25.search("mesh analysis", limit = 3)
            assertTrue(results.isNotEmpty())
            assertEquals("a", results.first().documentId)
        }

        test("a term in no document yields no results") {
            val bm25 = index("a" to "mesh analysis")
            assertEquals(0, bm25.search("quantum chromodynamics").size)
        }

        test("an empty index yields no results instead of crashing") {
            assertEquals(0, Bm25Index().search("anything").size)
            assertEquals(0, Bm25Index().search("").size)
        }

        test("rarity is rewarded over frequency of common terms") {
            val bm25 = index(
                "common" to "circuit circuit circuit circuit circuit",
                "rare" to "circuit thevenin",
            )
            val results = bm25.search("thevenin circuit")
            assertEquals("rare", results.first().documentId)
        }

        test("re-adding a document replaces it rather than double counting") {
            val bm25 = Bm25Index()
            bm25.add("a", "mesh analysis")
            bm25.add("a", "banana smoothie")
            assertEquals(1, bm25.documentCount)
            assertEquals(0, bm25.search("mesh analysis").size)
            assertTrue(bm25.search("banana smoothie").isNotEmpty())
        }

        test("removing a document removes its postings") {
            val bm25 = index("a" to "mesh analysis", "b" to "mesh currents")
            bm25.remove("a")
            assertEquals(1, bm25.documentCount)
            val results = bm25.search("mesh analysis")
            assertTrue(results.none { it.documentId == "a" })
        }

        test("clear empties the index") {
            val bm25 = index("a" to "mesh analysis")
            bm25.clear()
            assertEquals(0, bm25.documentCount)
            assertEquals(0, bm25.search("mesh").size)
        }

        test("rebuild replaces the whole index") {
            val bm25 = index("a" to "old content")
            bm25.rebuild(mapOf("x" to "mesh analysis", "y" to "thevenin theorem"))
            assertEquals(2, bm25.documentCount)
            assertEquals("x", bm25.search("mesh").first().documentId)
        }

        test("scores are non-negative") {
            val bm25 = index("a" to "mesh analysis", "b" to "mesh mesh mesh everywhere")
            bm25.search("mesh analysis everywhere").forEach {
                assertTrue(it.score >= 0.0, "negative score ${it.score} for ${it.documentId}")
            }
        }

        test("stemming lets inflected forms match") {
            val bm25 = index("a" to "three electronics assignments due friday")
            assertTrue(bm25.search("electronics assignment").isNotEmpty())
        }
    }
}
