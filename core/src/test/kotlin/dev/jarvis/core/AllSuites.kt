package dev.jarvis.core

import dev.jarvis.core.testing.TestSuite
import dev.jarvis.core.util.JsonSuite
import dev.jarvis.core.util.TextSuite

/**
 * The registry of every offline behavioural suite.
 *
 * Both runners consume this list:
 *  * `tools/local_core_check.sh` -> `LocalTestMain` (no test framework at all)
 *  * Gradle / CI               -> `CoreSelfTestJUnit` (thin JUnit bridge)
 *
 * Adding a suite means adding one line here, which guarantees the offline runner and CI
 * can never drift apart.
 */
object AllSuites {
    val all: List<TestSuite> = listOf(
        JsonSuite,
        TextSuite,
    )
}
