package dev.jarvis.app

import dev.jarvis.core.testing.TestRunner
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The only JUnit-dependent file in `:app`.
 *
 * It exists so `./gradlew :app:testDebugUnitTest` runs exactly the same assertions as
 * `tools/local_android_check.sh`. The filename ends in `JUnit` because the offline runner
 * excludes `*JUnit*` files (junit:junit is not reachable without Maven).
 */
class AppSelfTestJUnit {

    @Test
    fun runEntireOfflineSuite() {
        val report = TestRunner.run(AppAllSuites.all, verbose = true)
        println(report.summary())
        assertTrue(
            "${report.failures.size} app check(s) failed:\n" +
                report.failures.joinToString("\n") { " - ${it.suite} :: ${it.test} -- ${it.message}" },
            report.allPassed,
        )
    }
}
