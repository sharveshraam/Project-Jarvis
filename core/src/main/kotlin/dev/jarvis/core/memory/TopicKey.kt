package dev.jarvis.core.memory

import dev.jarvis.core.util.Text

/**
 * Topic keys.
 *
 * "Forget what I told you about X" only works if X can be matched against what was stored. So
 * every memory gets a normalised topic key derived from its text, and forgetting matches the
 * user's phrasing against those keys (and against the memory text itself, as a fallback).
 *
 * This is heuristic and is documented as heuristic: it strips a known set of memory-command
 * prefixes, drops stop words and stems the rest. It will get "my robotics project" and "the
 * robotics thing" close enough to match, and it will sometimes produce a key that reads oddly.
 * The recall path never relies on the key alone, which is what keeps a bad key from losing data.
 */
object TopicKey {

    /**
     * Leading phrases that introduce a memory rather than describe it. Longest first, so
     * "remember that" is stripped before "remember".
     */
    private val COMMAND_PREFIXES = listOf(
        "please remember that", "please remember", "can you remember that", "can you remember",
        "remember that", "remember", "keep in mind that", "keep in mind", "make a note that",
        "make a note of", "make a note", "note that", "note", "memorize", "memorise",
        "don't forget that", "don't forget", "do not forget that", "do not forget",
        "store this", "store", "save this", "save", "write down that", "write down",
        "jot down that", "jot down", "learn that", "learn",
    )

    /** Words that describe the act of remembering rather than the thing remembered. */
    private val META_WORDS = setOf(
        "remember", "remembered", "memory", "memories", "memorize", "memorise", "note",
        "notes", "noted", "forget", "forgot", "forgotten", "recall", "recalled", "store",
        "stored", "save", "saved", "learn", "learned", "told", "tell", "said", "say",
    )

    private const val MAX_TERMS = 6

    /**
     * Connectors that can follow a command prefix. "Remember to buy milk" and "remember that
     * the meeting moved" both describe a subject, and the connector is not part of it.
     */
    private val CONNECTORS = listOf("that", "to", "about", "of", "this", ",")

    /** Strips a memory-command prefix so the key describes the subject, not the instruction. */
    fun stripCommandPrefix(text: String): String {
        val normalized = Text.normalize(text)
        for (prefix in COMMAND_PREFIXES) {
            if (normalized.startsWith(prefix)) {
                val remainder = stripLeadingConnectors(normalized.removePrefix(prefix))
                if (remainder.isNotEmpty()) return remainder
            }
        }
        return normalized
    }

    /** Removes leading connectors and punctuation repeatedly, e.g. "that, to buy" -> "buy". */
    private fun stripLeadingConnectors(input: String): String {
        var current = input.trim()
        var changed = true
        while (changed) {
            changed = false
            for (connector in CONNECTORS) {
                if (current.startsWith("$connector ") || current == connector) {
                    current = current.removePrefix(connector).trim().removePrefix(",").trim()
                    changed = true
                }
            }
        }
        return current
    }

    /**
     * Phrases that introduce a *deletion* rather than a memory.
     *
     * Longest first. Kept separate from [COMMAND_PREFIXES] because "forget" is not a memory
     * prefix: stripping it there would corrupt "don't forget the meeting" into a subject while
     * leaving a genuine deletion request unstripped.
     */
    private val FORGET_PREFIXES = listOf(
        "please forget everything you know about", "please forget what i told you about",
        "forget everything you know about", "forget what i told you about",
        "forget everything about", "forget all about", "delete everything about",
        "delete what you remember about", "remove everything about", "remove what you know about",
        "erase everything about", "unremember", "forget about", "forget", "delete", "remove",
        "erase", "wipe",
    )

    /** Trailing noise that adds nothing to the subject of a deletion request. */
    private val TRAILING_NOISE = listOf(
        "from your memory", "from memory", "completely", "entirely", "please",
        "if you can", "right now", "now",
    )

    /**
     * Extracts the *subject* of a deletion request.
     *
     * "Forget what I told you about the robotics project" -> "robotics project". Without this,
     * the meta-words dilute the similarity score below any sane threshold and the deletion
     * silently does nothing - which would be the worst possible failure mode for a privacy
     * control, because the assistant would appear to comply.
     */
    fun stripForgetPrefix(text: String): String {
        val normalized = Text.normalize(text)
        for (prefix in FORGET_PREFIXES) {
            if (normalized.startsWith(prefix)) {
                val remainder = stripLeadingArticles(stripLeadingConnectors(normalized.removePrefix(prefix)))
                if (remainder.isNotEmpty()) return stripTrailingNoise(remainder)
            }
        }
        // No deletion phrase matched: this is not a deletion request, so the text is left
        // alone apart from trailing noise. Stripping "my" here would rewrite an ordinary
        // subject phrase the user never asked to have rewritten.
        return stripTrailingNoise(normalized)
    }

    /**
     * Leading determiners, for the deletion path only.
     *
     * "Forget the robotics project" and "forget robotics project" must produce the same
     * subject. [TopicKey.of] drops stop words anyway, so this is about making the reported
     * subject read naturally in "I forgot: robotics project".
     */
    private val LEADING_ARTICLES = listOf("the", "a", "an", "my", "your", "our", "all", "any")

    private fun stripLeadingArticles(input: String): String {
        var current = input.trim()
        var changed = true
        while (changed) {
            changed = false
            for (article in LEADING_ARTICLES) {
                if (current.startsWith("$article ")) {
                    current = current.removePrefix("$article ").trim()
                    changed = true
                }
            }
        }
        return current
    }

    private fun stripTrailingNoise(input: String): String {
        var current = input.trim()
        var changed = true
        while (changed) {
            changed = false
            for (noise in TRAILING_NOISE) {
                if (current.endsWith(" $noise")) {
                    current = current.removeSuffix(" $noise").trim()
                    changed = true
                }
            }
        }
        return current
    }

    /**
     * A normalised, order-preserving topic key, e.g.
     * "Remember that I'm working on an ECE robotics project" -> "work ece robotics project".
     */
    fun of(text: String, maxTerms: Int = MAX_TERMS): String {
        val subject = stripCommandPrefix(text)
        val terms = Text.stems(Text.keywords(subject))
            .filter { it.isNotBlank() && it !in META_WORDS && it.length > 1 }
            .distinct()
            .take(maxTerms)
        return terms.joinToString(" ")
    }

    /** Human-readable topic, using the user's own words rather than stems. */
    fun display(text: String, maxTerms: Int = MAX_TERMS): String {
        val subject = stripCommandPrefix(text)
        val words = Text.keywords(subject)
            .filter { it.isNotBlank() && Text.stem(it) !in META_WORDS }
            .distinctBy { Text.stem(it) }
            .take(maxTerms)
        return words.joinToString(" ")
    }

    /**
     * Score for how well [query] describes [candidate].
     *
     * Combines phrase similarity with containment, because users shorten: stored key
     * "work ece robotics project", spoken query "robotics project". Containment alone would
     * make any subset match perfectly, so it is capped below 1.0.
     */
    fun matchScore(query: String, candidate: String): Double {
        if (candidate.isBlank() || query.isBlank()) return 0.0
        val phrase = Text.phraseSimilarity(query, candidate)
        val queryTerms = Text.stems(Text.keywords(query)).toSet()
        val candidateTerms = Text.stems(Text.keywords(candidate)).toSet()
        if (queryTerms.isEmpty() || candidateTerms.isEmpty()) return phrase
        val contained = queryTerms.all { it in candidateTerms }
        val overlap = queryTerms.intersect(candidateTerms).size.toDouble() / queryTerms.size
        val containmentScore = if (contained) 0.85 else 0.6 * overlap
        return maxOf(phrase, containmentScore)
    }

    /** True when [query] refers to the same subject as [candidate]. */
    fun matches(query: String, candidate: String, threshold: Double = 0.55): Boolean =
        matchScore(query, candidate) >= threshold
}
