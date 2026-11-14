package dev.jarvis.app.channels

import dev.jarvis.app.JarvisApplication
import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertTrue

/**
 * Contract tests for the notification channels.
 *
 * These are pure-logic checks (constants only), so they run offline against android.jar
 * without ever invoking a framework method - which matters because android.jar methods
 * throw at runtime.
 */
object ChannelContractSuite : Suite("app/notification-channels") {
    init {
        test("channel ids are distinct") {
            val ids = setOf(
                JarvisApplication.CHANNEL_ASSISTANT,
                JarvisApplication.CHANNEL_REMINDERS,
                JarvisApplication.CHANNEL_AUTOMATION,
            )
            // Duplicate ids would silently merge unrelated notifications, and on
            // Android 8+ a notification posted to a channel that was never created is
            // dropped without any error - reminders would vanish with no trace.
            assertEquals(3, ids.size)
        }

        test("channel ids are stable") {
            // Channel ids are a persisted contract with the user's notification settings:
            // changing one orphans their chosen importance, sound and badge for it.
            assertEquals("jarvis.assistant", JarvisApplication.CHANNEL_ASSISTANT)
            assertEquals("jarvis.reminders", JarvisApplication.CHANNEL_REMINDERS)
            assertEquals("jarvis.automation", JarvisApplication.CHANNEL_AUTOMATION)
        }

        test("channel ids are namespaced and non blank") {
            listOf(
                JarvisApplication.CHANNEL_ASSISTANT,
                JarvisApplication.CHANNEL_REMINDERS,
                JarvisApplication.CHANNEL_AUTOMATION,
            ).forEach {
                assertTrue(it.isNotBlank(), "blank channel id")
                assertTrue(it.startsWith("jarvis."), "channel id '$it' is not namespaced")
                assertTrue(!it.contains(' '), "channel id '$it' contains a space")
            }
        }
    }
}
