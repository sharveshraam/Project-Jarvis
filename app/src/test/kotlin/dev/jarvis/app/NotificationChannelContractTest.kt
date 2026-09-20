package dev.jarvis.app

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Plain JVM unit tests for the Android module.
 *
 * These run without a device and without Robolectric, so they only cover logic that does
 * not touch the framework. Behaviour that needs Android itself is covered by the core
 * suite (which is framework-free by design) and by instrumentation tests.
 */
class NotificationChannelContractTest {

    @Test
    fun `channel ids are distinct`() {
        val ids = setOf(
            JarvisApplication.CHANNEL_ASSISTANT,
            JarvisApplication.CHANNEL_REMINDERS,
            JarvisApplication.CHANNEL_AUTOMATION,
        )
        // Duplicate channel ids would silently merge unrelated notifications, and on
        // Android 8+ a notification posted to a channel that was never created is
        // dropped without any error - so reminders would vanish with no trace.
        assertEquals(3, ids.size)
    }

    @Test
    fun `channel ids are stable`() {
        // Channel ids are part of the app's persisted contract: changing one orphans the
        // user's existing notification settings (importance, sound, badge) for it.
        assertEquals("jarvis.assistant", JarvisApplication.CHANNEL_ASSISTANT)
        assertEquals("jarvis.reminders", JarvisApplication.CHANNEL_REMINDERS)
        assertEquals("jarvis.automation", JarvisApplication.CHANNEL_AUTOMATION)
    }

    @Test
    fun `channel ids are non blank`() {
        listOf(
            JarvisApplication.CHANNEL_ASSISTANT,
            JarvisApplication.CHANNEL_REMINDERS,
            JarvisApplication.CHANNEL_AUTOMATION,
        ).forEach { assertTrue(it.isNotBlank()) }
    }
}
