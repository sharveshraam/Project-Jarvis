plugins {
    alias(libs.plugins.kotlin.jvm)
}

// --------------------------------------------------------------------------------------
// :core is deliberately DEPENDENCY-FREE.
//
// It compiles against the Kotlin standard library only. No Android, no coroutines,
// no JSON library, no DI framework. That is a hard architectural rule, because it
// means the entire assistant brain can be compiled and unit-tested on a plain JVM
// with nothing but `kotlinc` - no Android SDK, no network, no Gradle:
//
//     ./tools/local_core_check.sh
//
// Anything that needs to talk to the outside world (disk, network, Android APIs,
// clocks, randomness) is expressed here as a *port* (interface) and implemented by
// an adapter in :app. That is also what makes every subsystem replaceable, which
// the project requirements explicitly ask for.
// --------------------------------------------------------------------------------------

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    jvmToolchain(17)
    compilerOptions {
        // Treat warnings as warnings, not errors: the public API surface is large
        // and an over-strict setting makes incremental contribution painful.
        freeCompilerArgs.add("-Xjvm-default=all")
    }
}

dependencies {
    // Intentionally empty at runtime. Tests only.
    testImplementation(libs.junit)
}

tasks.named<Test>("test") {
    // The full behavioural suite lives in `CoreSelfTest` and is written without any
    // test framework so the very same suite can be executed by
    // ./tools/local_core_check.sh on a machine that has no Maven access.
    // `CoreSelfTestJUnit` is the thin JUnit bridge used by Gradle/CI.
    testLogging {
        events("passed", "skipped", "failed")
        showStandardStreams = true
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}
