pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "Jarvis"

// :core is a pure Kotlin/JVM module with ZERO Android dependencies.
// Every piece of assistant "intelligence" (NLU, planning, memory, retrieval,
// tools, automation, teaching, safety) lives there so it can be compiled and
// unit-tested on a plain JVM without the Android SDK.
//
// :app is the Android shell: it adapts Android platform services
// (AccessibilityService, SQLite, TTS/ASR, notifications, sensors) to the
// ports declared by :core.
include(":core")
include(":app")
