package dev.jarvis.core.testing

/**
 * A microscopic test framework.
 *
 * The `:core` module is required to have zero dependencies so that it can be compiled
 * and tested with nothing but a Kotlin compiler and a Java runtime (see
 * `tools/local_core_check.sh`). That rules out pulling in JUnit for the canonical suite.
 *
 * Instead every behavioural test is written against this harness, and a single thin
 * JUnit bridge (`CoreSelfTestJUnit`) delegates to it so that Gradle and CI run exactly
 * the same assertions as the offline runner.
 */

/** A single failed check. */
data class TestFailure(
    val suite: String,
    val test: String,
    val message: String,
    val throwableClass: String? = null,
)

/** A named group of checks. */
interface TestSuite {
    val name: String
    fun cases(): List<TestCase>
}

data class TestCase(val name: String, val body: () -> Unit)

/**
 * Base class for a suite. Subclasses register cases from their `init` block:
 *
 * ```
 * object MemorySuite : Suite("memory") {
 *     init {
 *         test("stores an explicit fact") { ... }
 *     }
 * }
 * ```
 */
abstract class Suite(override val name: String) : TestSuite {
    private val registered = mutableListOf<TestCase>()

    protected fun test(name: String, body: () -> Unit) {
        registered += TestCase(name, body)
    }

    override fun cases(): List<TestCase> = registered.toList()
}

class AssertionError(message: String) : Error(message)

fun fail(message: String): Nothing = throw AssertionError(message)

fun assertTrue(condition: Boolean, message: String = "expected true") {
    if (!condition) fail(message)
}

fun assertFalse(condition: Boolean, message: String = "expected false") {
    if (condition) fail(message)
}

fun <T> assertEquals(expected: T, actual: T, message: String? = null) {
    if (expected != actual) {
        val prefix = if (message != null) "$message -- " else ""
        fail("${prefix}expected <$expected> but was <$actual>")
    }
}

fun assertNotEquals(unexpected: Any?, actual: Any?, message: String? = null) {
    if (unexpected == actual) {
        val prefix = if (message != null) "$message -- " else ""
        fail("${prefix}expected value to differ from <$unexpected>")
    }
}

fun assertNull(value: Any?, message: String = "expected null but was <$value>") {
    if (value != null) fail(message)
}

fun <T : Any> assertNotNull(value: T?, message: String = "expected non-null"): T {
    if (value == null) fail(message)
    return value
}

fun assertContains(haystack: String, needle: String, message: String? = null) {
    if (!haystack.contains(needle, ignoreCase = true)) {
        val prefix = if (message != null) "$message -- " else ""
        fail("${prefix}expected to contain <$needle> but was <$haystack>")
    }
}

fun assertDoesNotContain(haystack: String, needle: String, message: String? = null) {
    if (haystack.contains(needle, ignoreCase = true)) {
        val prefix = if (message != null) "$message -- " else ""
        fail("${prefix}expected NOT to contain <$needle> but was <$haystack>")
    }
}

fun <T> assertContainsItem(collection: Collection<T>, item: T, message: String? = null) {
    if (item !in collection) {
        val prefix = if (message != null) "$message -- " else ""
        fail("${prefix}expected collection to contain <$item> but was <$collection>")
    }
}

fun assertGreaterThan(actual: Int, floor: Int, message: String? = null) {
    if (actual <= floor) {
        val prefix = if (message != null) "$message -- " else ""
        fail("${prefix}expected <$actual> to be greater than <$floor>")
    }
}

/** Asserts that [body] throws, and returns the message of the thrown error. */
fun assertThrows(body: () -> Unit): String {
    try {
        body()
    } catch (t: Throwable) {
        return t.message ?: t::class.simpleName.orEmpty()
    }
    fail("expected an exception but none was thrown")
}

/** Result of running a set of suites. */
class TestReport(
    val passed: Int,
    val failures: List<TestFailure>,
    val durationMillis: Long,
) {
    val total: Int get() = passed + failures.size
    val allPassed: Boolean get() = failures.isEmpty()

    fun summary(): String = buildString {
        appendLine("---------------------------------------------------------------")
        if (failures.isEmpty()) {
            appendLine("PASSED  $passed/$total checks in ${durationMillis}ms")
        } else {
            appendLine("FAILED  $passed passed, ${failures.size} failed, $total total (${durationMillis}ms)")
            appendLine()
            failures.forEachIndexed { index, failure ->
                appendLine("  ${index + 1}) ${failure.suite} :: ${failure.test}")
                appendLine("     ${failure.message}")
                if (failure.throwableClass != null) appendLine("     thrown: ${failure.throwableClass}")
            }
        }
        appendLine("---------------------------------------------------------------")
    }
}

object TestRunner {
    fun run(suites: List<TestSuite>, verbose: Boolean = true): TestReport {
        val started = System.currentTimeMillis()
        val failures = mutableListOf<TestFailure>()
        var passed = 0
        var totalCases = 0

        for (suite in suites) {
            val cases = suite.cases()
            totalCases += cases.size
            var suiteFailures = 0
            for (case in cases) {
                try {
                    case.body()
                    passed++
                } catch (t: Throwable) {
                    suiteFailures++
                    failures += TestFailure(
                        suite = suite.name,
                        test = case.name,
                        message = t.message ?: "(no message)",
                        throwableClass = if (t is AssertionError) null else t::class.qualifiedName,
                    )
                }
            }
            if (verbose) {
                val mark = if (suiteFailures == 0) "ok  " else "FAIL"
                println("[$mark] ${suite.name} (${cases.size - suiteFailures}/${cases.size})")
            }
        }

        return TestReport(passed, failures, System.currentTimeMillis() - started).also {
            if (verbose) println("suites=${suites.size} cases=$totalCases")
        }
    }
}
