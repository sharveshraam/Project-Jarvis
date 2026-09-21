package dev.jarvis.core.util

/**
 * Text normalisation and similarity helpers.
 *
 * The assistant matches user language against app names, on-screen labels, contacts,
 * automation triggers and stored memories. Real-world input is messy: different case,
 * punctuation, contractions, pluralisation, typos and word-order variation. Everything
 * that has to tolerate that mess is centralised here so the behaviour is identical in
 * NLU, memory retrieval, semantic UI matching and the knowledge base.
 */
object Text {

    private val STOP_WORDS = setOf(
        "a", "an", "the", "and", "or", "but", "if", "then", "than", "so", "to", "of", "in",
        "on", "at", "by", "for", "with", "about", "into", "through", "during", "before",
        "after", "above", "below", "from", "up", "down", "out", "off", "over", "under",
        "again", "further", "once", "here", "there", "when", "where", "why", "how", "all",
        "any", "both", "each", "few", "more", "most", "other", "some", "such", "no", "nor",
        "not", "only", "own", "same", "too", "very", "s", "t", "just", "don", "now",
        "please", "could", "would", "should", "will", "shall", "may", "might", "can",
        "do", "does", "did", "doing", "is", "are", "was", "were", "be", "been", "being",
        "am", "i", "me", "my", "myself", "we", "our", "you", "your", "he", "him", "his",
        "she", "her", "it", "its", "they", "them", "their", "what", "which", "who", "whom",
        "this", "that", "these", "those", "hey", "ok", "okay", "yeah", "bro", "dude",
        // Contractions carry no topical content but survive normalisation, because dropping
        // apostrophes would turn "i'm" into two tokens and "don't" into "don" + "t".
        "i'm", "it's", "that's", "there's", "here's", "what's", "who's", "let's",
        "don't", "doesn't", "didn't", "can't", "cannot", "won't", "wouldn't", "shouldn't",
        "isn't", "aren't", "wasn't", "weren't", "haven't", "hasn't", "hadn't",
        "i'll", "you'll", "he'll", "she'll", "we'll", "they'll", "it'll",
        "i've", "you've", "we've", "they've", "you're", "we're", "they're",
    )

    /**
     * Lower-cases, replaces punctuation with spaces and collapses whitespace.
     *
     * Apostrophes are deliberately preserved so contractions survive as single tokens
     * ("i'll", "don't") - they carry meaning for intent parsing. Hyphens become spaces so
     * "state-of-the-art" tokenises into matchable words.
     */
    fun normalize(input: String): String {
        val lowered = input.lowercase()
        val sb = StringBuilder(lowered.length)
        for (ch in lowered) {
            sb.append(
                when {
                    ch.isLetterOrDigit() -> ch
                    // Curly/typographic apostrophes fold onto the ASCII one.
                    ch == '\'' || ch == '\u2019' || ch == '\u2018' || ch == '\u02BC' -> '\''
                    else -> ' '
                },
            )
        }
        return sb.toString().trim().replace(Regex("\\s+"), " ")
    }

    /** Splits normalised text into tokens. */
    fun tokenize(input: String): List<String> =
        normalize(input).split(' ').filter { it.isNotBlank() }

    /** Tokens with stop words removed; falls back to all tokens if that would empty the list. */
    fun keywords(input: String): List<String> {
        val tokens = tokenize(input)
        val meaningful = tokens.filter { it !in STOP_WORDS && it.length > 1 }
        return if (meaningful.isEmpty()) tokens else meaningful
    }

    fun isStopWord(token: String): Boolean = token in STOP_WORDS

    /**
     * Words whose trailing "s" is part of the stem and must not be stripped.
     */
    private val KEEP_TRAILING_S = setOf(
        "news", "bus", "gas", "lens", "series", "species", "atlas", "canvas",
        "bias", "alias", "status", "campus", "focus", "bonus", "corpus", "ios",
        "sms", "glasses", "scissors", "physics", "mathematics", "economics",
        "ethics", "electronics", "robotics", "informatics",
    )

    /**
     * Very light English suffix stripper.
     *
     * Deliberately crude, and honest about it: "messages"/"message", "reminding"/"remind"
     * and "studies"/"study" need to collapse onto the same stem for memory retrieval and
     * on-screen label matching to feel natural. This is not a linguistic stemmer
     * (no Porter/Porter2 guarantees) - it is a normaliser tuned for the assistant's
     * matching problems, with an exception list for words whose trailing "s" is real.
     */
    fun stem(token: String): String {
        val word = normalize(token).replace("'", "")
        if (word.length <= 3 || word in KEEP_TRAILING_S) return word

        // Plurals and third-person singular.
        if (word.endsWith("ies") && word.length >= 5) return word.dropLast(3) + "y"
        if (word.endsWith("sses") || word.endsWith("shes") ||
            word.endsWith("ches") || word.endsWith("xes") || word.endsWith("zes")
        ) {
            return word.dropLast(2)
        }
        if (word.endsWith("s") && !word.endsWith("ss") && !word.endsWith("us") && !word.endsWith("is")) {
            return word.dropLast(1)
        }

        // Gerunds / participles.
        if (word.endsWith("ing") && word.length >= 6) {
            val base = word.dropLast(3)
            return undouble(base)
        }

        // Past tense and adverbs.
        if (word.endsWith("edly") && word.length >= 6) return word.dropLast(4)
        if (word.endsWith("ed") && word.length >= 5) return undouble(word.dropLast(2))
        if (word.endsWith("ly") && word.length >= 5) return word.dropLast(2)

        return word
    }

    /** "runn" -> "run", "stopp" -> "stop"; leaves doubled vowels alone ("see"). */
    private fun undouble(base: String): String {
        if (base.length < 3) return base
        val last = base[base.length - 1]
        val prev = base[base.length - 2]
        return if (last == prev && last !in "aeiou") base.dropLast(1) else base
    }

    fun stems(tokens: List<String>): List<String> = tokens.map { stem(it) }

    /** Levenshtein edit distance, iterative with two rows (no quadratic memory). */
    fun levenshtein(a: String, b: String): Int {
        if (a == b) return 0
        if (a.isEmpty()) return b.length
        if (b.isEmpty()) return a.length
        var previous = IntArray(b.length + 1) { it }
        var current = IntArray(b.length + 1)
        for (i in 1..a.length) {
            current[0] = i
            for (j in 1..b.length) {
                val cost = if (a[i - 1] == b[j - 1]) 0 else 1
                current[j] = minOf(
                    current[j - 1] + 1,
                    previous[j] + 1,
                    previous[j - 1] + cost,
                )
            }
            val swap = previous
            previous = current
            current = swap
        }
        return previous[b.length]
    }

    /** 0..1 normalised string similarity based on edit distance. */
    fun stringSimilarity(a: String, b: String): Double {
        val left = normalize(a)
        val right = normalize(b)
        if (left.isEmpty() && right.isEmpty()) return 1.0
        if (left.isEmpty() || right.isEmpty()) return 0.0
        if (left == right) return 1.0
        val distance = levenshtein(left, right)
        val longest = maxOf(left.length, right.length)
        return (1.0 - distance.toDouble() / longest).coerceIn(0.0, 1.0)
    }

    /**
     * 0..1 similarity between two phrases.
     *
     * Two failure modes have to be handled at once, and a single metric cannot do it:
     *
     *  1. Reordering. "youtube music" vs "music youtube" must score ~1.0. Character
     *     similarity is useless here (edit distance is nearly maximal), so when the token
     *     sets overlap the score is driven by lexical overlap.
     *  2. Typos and inflection. "youtub" vs "youtube", "youtube musc" vs "YouTube Music"
     *     share few or no identical tokens, so when the token sets do NOT overlap the
     *     score falls back to character similarity.
     *
     * A length-ratio factor stops a short candidate from winning just by being a subset:
     * for the query "youtube musc" the candidate "YouTube" must not beat "YouTube Music".
     */
    fun phraseSimilarity(a: String, b: String): Double {
        val leftTokens = stems(keywords(a))
        val rightTokens = stems(keywords(b))
        if (leftTokens.isEmpty() || rightTokens.isEmpty()) return stringSimilarity(a, b)

        val leftSet = leftTokens.toSet()
        val rightSet = rightTokens.toSet()
        val intersection = leftSet.intersect(rightSet).size
        val jaccardScore = jaccard(leftSet, rightSet)
        val containment = intersection.toDouble() / minOf(leftSet.size, rightSet.size)
        val lengthRatio = minOf(leftSet.size, rightSet.size).toDouble() / maxOf(leftSet.size, rightSet.size)

        // Average best-character-similarity per left token: gives partial credit for
        // near-miss tokens even when no token matches exactly.
        val fuzzyTokenScore = leftTokens
            .map { left -> rightTokens.maxOf { stringSimilarity(left, it) } }
            .average()

        val lexical = (0.60 * jaccardScore + 0.25 * containment + 0.15 * fuzzyTokenScore) *
            (0.5 + 0.5 * lengthRatio)
        val character = stringSimilarity(a, b)

        return if (intersection > 0) {
            maxOf(lexical, 0.35 * character + 0.65 * lexical)
        } else {
            maxOf(0.95 * character, 0.60 * fuzzyTokenScore)
        }.coerceIn(0.0, 1.0)
    }

    fun jaccard(a: Set<String>, b: Set<String>): Double {
        if (a.isEmpty() && b.isEmpty()) return 0.0
        val union = (a + b).size
        if (union == 0) return 0.0
        return a.intersect(b).size.toDouble() / union
    }

    /** True when [needle] appears in [haystack] allowing for a small edit distance. */
    fun fuzzyContains(haystack: String, needle: String, threshold: Double = 0.72): Boolean {
        val hay = normalize(haystack)
        val nee = normalize(needle)
        if (nee.isEmpty()) return true
        if (hay.contains(nee)) return true
        val hayTokens = tokenize(hay)
        val neeTokens = tokenize(nee)
        if (neeTokens.size == 1) {
            return hayTokens.any { stringSimilarity(it, neeTokens[0]) >= threshold }
        }
        // Sliding window over the haystack for multi-word needles.
        for (start in 0..maxOf(0, hayTokens.size - neeTokens.size)) {
            val window = hayTokens.subList(start, minOf(hayTokens.size, start + neeTokens.size))
            if (window.size == neeTokens.size &&
                phraseSimilarity(window.joinToString(" "), nee) >= threshold
            ) {
                return true
            }
        }
        return false
    }

    /** Best-scoring candidate from [candidates] for [query], or null when nothing clears [threshold]. */
    fun bestMatch(query: String, candidates: List<String>, threshold: Double = 0.5): Pair<String, Double>? {
        var best: Pair<String, Double>? = null
        for (candidate in candidates) {
            val score = phraseSimilarity(query, candidate)
            if (score >= threshold && (best == null || score > best.second)) best = candidate to score
        }
        return best
    }

    fun truncate(input: String, max: Int): String =
        if (input.length <= max) input else input.take(max - 1).trimEnd() + "\u2026"

    /** Removes surrounding quotes and trims. Used when extracting dictated message bodies. */
    fun stripQuotes(input: String): String {
        val trimmed = input.trim()
        if (trimmed.length >= 2) {
            val first = trimmed.first()
            val last = trimmed.last()
            if ((first == '"' && last == '"') || (first == '\'' && last == '\'')) {
                return trimmed.substring(1, trimmed.length - 1).trim()
            }
        }
        return trimmed
    }

    /**
     * Extracts a quoted span from [input] if present, otherwise returns null.
     * Used for `send John a message saying "I'll be there at 6"`.
     */
    fun firstQuotedSpan(input: String): String? {
        val pairs = listOf('"' to '"', '\'' to '\'', '\u201C' to '\u201D')
        for ((open, close) in pairs) {
            val start = input.indexOf(open)
            if (start < 0) continue
            val end = input.indexOf(close, start + 1)
            if (end > start + 1) return input.substring(start + 1, end).trim()
        }
        return null
    }

    /** Ordinal words to 1-based indices, for "the second video" / "the 3rd result". */
    fun ordinalToIndex(word: String): Int? = when (normalize(word).removeSuffix(".")) {
        "first", "1st", "one", "1" -> 1
        "second", "2nd", "two", "2" -> 2
        "third", "3rd", "three", "3" -> 3
        "fourth", "4th", "four", "4" -> 4
        "fifth", "5th", "five", "5" -> 5
        "sixth", "6th", "six", "6" -> 6
        "seventh", "7th", "seven", "7" -> 7
        "eighth", "8th", "eight", "8" -> 8
        "ninth", "9th", "nine", "9" -> 9
        "tenth", "10th", "ten", "10" -> 10
        else -> normalize(word).removeSuffix("th").removeSuffix("st").removeSuffix("nd")
            .removeSuffix("rd").toIntOrNull()
    }

    /** Spoken-form numbers up to twenty, plus multiples of ten to a hundred. */
    fun wordToNumber(word: String): Int? = when (normalize(word)) {
        "zero" -> 0
        "one", "a" -> 1
        "two", "couple" -> 2
        "three" -> 3
        "four" -> 4
        "five" -> 5
        "six" -> 6
        "seven" -> 7
        "eight" -> 8
        "nine" -> 9
        "ten" -> 10
        "eleven" -> 11
        "twelve", "dozen" -> 12
        "thirteen" -> 13
        "fourteen" -> 14
        "fifteen" -> 15
        "sixteen" -> 16
        "seventeen" -> 17
        "eighteen" -> 18
        "nineteen" -> 19
        "twenty" -> 20
        "thirty" -> 30
        "forty" -> 40
        "fifty" -> 50
        "sixty" -> 60
        "half" -> 30
        "quarter" -> 15
        else -> normalize(word).toIntOrNull()
    }
}
