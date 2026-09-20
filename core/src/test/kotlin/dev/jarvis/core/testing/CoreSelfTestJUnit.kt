package dev.jarvis.core.testing

import dev.jarvis.core.AllSuites
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The only test-framework-dependent file in `:core`.
 *
 * It exists so `./gradlew :core:test` and the offline runner in
 * `tools/local_core_check.sh` execute the *same* assertions. The filename ends in
 * `JUnit` because the offline runner excludes `*JUnit*` files (junit:junit is not
 * reachable without Maven).
 */
class CoreSelfTestJUnit {

    @Test
    fun runEntireOfflineSuite() {
        val report = TestRunner.run(AllSuites.all, verbose = true)
        println(report.summary())
        assertTrue(
            "${report.failures.size} core check(s) failed:\n" +
                report.failures.joinToString("\n") { " - ${it.suite} :: ${it.test} -- ${it.message}" },
            report.allPassed,
        )
    }
}
