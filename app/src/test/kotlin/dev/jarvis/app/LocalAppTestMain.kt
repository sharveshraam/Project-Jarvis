package dev.jarvis.app

import dev.jarvis.core.testing.TestRunner

/** Entry point for the `:app` phase of `tools/local_android_check.sh`. */
fun main() {
    println("Jarvis app - offline suite (compiled against android.jar, run on the JVM)")
    println()
    val report = TestRunner.run(AppAllSuites.all)
    println()
    print(report.summary())
    if (!report.allPassed) {
        kotlin.system.exitProcess(1)
    }
}
