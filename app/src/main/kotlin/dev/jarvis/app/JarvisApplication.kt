package dev.jarvis.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build

/**
 * Composition root.
 *
 * Jarvis uses hand-wired dependency injection rather than an annotation processor: the
 * object graph is small, it must be constructible from a Service (not just an Activity),
 * and avoiding kapt/ksp keeps the build reproducible offline. Everything the assistant
 * needs is reachable from [graph].
 */
class JarvisApplication : Application() {

    /** Built lazily so tests and previews can inject their own. */
    val graph: JarvisGraph by lazy { JarvisGraph.create(this) }

    override fun onCreate() {
        super.onCreate()
        instance = this
        createNotificationChannels()
    }

    /**
     * Notification channels are created eagerly and with user-facing names, because on
     * Android 8+ a notification posted to a missing channel is silently dropped - which
     * would make reminders and automation results disappear without a trace.
     */
    private fun createNotificationChannels() {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channels = listOf(
                NotificationChannel(
                    CHANNEL_ASSISTANT,
                    getString(R.string.channel_assistant_name),
                    NotificationManager.IMPORTANCE_LOW,
                ).apply {
                    description = getString(R.string.channel_assistant_description)
                    setShowBadge(false)
                },
                NotificationChannel(
                    CHANNEL_REMINDERS,
                    getString(R.string.channel_reminders_name),
                    NotificationManager.IMPORTANCE_HIGH,
                ).apply {
                    description = getString(R.string.channel_reminders_description)
                },
                NotificationChannel(
                    CHANNEL_AUTOMATION,
                    getString(R.string.channel_automation_name),
                    NotificationManager.IMPORTANCE_DEFAULT,
                ).apply {
                    description = getString(R.string.channel_automation_description)
                },
            )
            channels.forEach { manager.createNotificationChannel(it) }
        }
    }

    companion object {
        const val CHANNEL_ASSISTANT = "jarvis.assistant"
        const val CHANNEL_REMINDERS = "jarvis.reminders"
        const val CHANNEL_AUTOMATION = "jarvis.automation"

        @Volatile
        private var instance: JarvisApplication? = null

        /**
         * Access point for components the framework constructs itself (services,
         * receivers) which cannot receive constructor injection.
         */
        fun get(context: Context): JarvisApplication =
            instance ?: (context.applicationContext as JarvisApplication).also { instance = it }
    }
}
