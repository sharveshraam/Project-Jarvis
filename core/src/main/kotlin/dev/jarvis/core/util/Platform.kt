package dev.jarvis.core.util

/**
 * Ports for the two sources of nondeterminism the assistant touches: time and identity.
 *
 * Injecting them keeps `:core` testable (a fixed clock makes "remind me after dinner"
 * assertable) and keeps Android's `System.currentTimeMillis` / `UUID` out of the brain.
 */

/** Abstraction over wall-clock time and the user's timezone offset. */
interface ClockPort {
    fun nowMillis(): Long

    /** Offset from UTC in seconds, e.g. Asia/Kolkata is +19800. */
    fun zoneOffsetSeconds(): Int

    /** 0-23 local hour of day. */
    fun localHour(nowMillis: Long = nowMillis()): Int {
        val shifted = nowMillis + zoneOffsetSeconds() * 1000L
        val hours = Math.floorMod(shifted / MILLIS_PER_HOUR, 24L)
        return hours.toInt()
    }

    /** 0=Monday .. 6=Sunday, matching java.time.DayOfWeek ordinal conventions. */
    fun localDayOfWeek(nowMillis: Long = nowMillis()): Int {
        val shifted = nowMillis + zoneOffsetSeconds() * 1000L
        val days = Math.floorDiv(shifted, MILLIS_PER_DAY)
        // 1970-01-01 was a Thursday; day 0 -> index 3.
        return Math.floorMod(days + 3, 7L).toInt()
    }

    fun localMinute(nowMillis: Long = nowMillis()): Int {
        val shifted = nowMillis + zoneOffsetSeconds() * 1000L
        return Math.floorMod(shifted / MILLIS_PER_MINUTE, 60L).toInt()
    }

    companion object {
        const val MILLIS_PER_MINUTE = 60_000L
        const val MILLIS_PER_HOUR = 3_600_000L
        const val MILLIS_PER_DAY = 86_400_000L
    }
}

/** A clock pinned to a moment, for deterministic tests and previews. */
class FixedClock(
    private var millis: Long,
    private val offsetSeconds: Int = 0,
) : ClockPort {
    override fun nowMillis(): Long = millis
    override fun zoneOffsetSeconds(): Int = offsetSeconds
    fun advance(byMillis: Long) { millis += byMillis }
    fun set(toMillis: Long) { millis = toMillis }
}

/**
 * A clock that reads the JVM system time.
 *
 * Safe to use from `:core` because `System.currentTimeMillis()` is a JDK primitive, not
 * an Android API. `:app` supplies its own implementation when it needs the device's real
 * timezone offset.
 */
class SystemClock(private val offsetSecondsProvider: () -> Int = { 0 }) : ClockPort {
    override fun nowMillis(): Long = System.currentTimeMillis()
    override fun zoneOffsetSeconds(): Int = offsetSecondsProvider()
}

/** Generates stable, unique identifiers. */
interface IdPort {
    fun newId(prefix: String = "id"): String
}

/** Counter-based ids: predictable, collision-free within a process, ideal for tests. */
class SequentialIds(private var start: Int = 1) : IdPort {
    override fun newId(prefix: String): String = "$prefix-${start++}"
}

/** UUID-backed ids for production use. */
class RandomIds : IdPort {
    override fun newId(prefix: String): String = "$prefix-${java.util.UUID.randomUUID()}"
}

/** Deterministic byte source; separated so embeddings/hashing can be tested exactly. */
interface HashPort {
    fun sha256Hex(input: String): String
}
