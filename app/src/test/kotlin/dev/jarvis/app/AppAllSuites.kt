package dev.jarvis.app

import dev.jarvis.app.channels.ChannelContractSuite
import dev.jarvis.core.testing.TestSuite

/**
 * Registry of every `:app` offline suite, mirroring `dev.jarvis.core.AllSuites`.
 *
 * Both runners consume this list:
 *  * `tools/local_android_check.sh` -> `LocalAppTestMain`
 *  * Gradle / CI                    -> `AppSelfTestJUnit`
 *
 * Suites here must be pure logic: they are compiled against android.jar but executed on a
 * desktop JVM, where every framework method body throws. Anything that genuinely needs
 * Android belongs in an instrumentation test instead.
 */
object AppAllSuites {
    val all: List<TestSuite> = listOf(
        ChannelContractSuite,
    )
}
