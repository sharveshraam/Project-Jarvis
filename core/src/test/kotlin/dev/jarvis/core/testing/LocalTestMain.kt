package dev.jarvis.core.testing

import dev.jarvis.core.AllSuites

/**
 * Entry point for `tools/local_core_check.sh`.
 *
 * Runs the complete offline suite and exits non-zero on any failure so CI fails loudly.
 */
fun main() {
    println("Jarvis core - offline behavioural suite")
    println("(no Android SDK, no Gradle, no network)")
    println()
    val report = TestRunner.run(AllSuites.all)
    println()
    print(report.summary())
    if (!report.allPassed) {
        kotlin.system.exitProcess(1)
    }
}
