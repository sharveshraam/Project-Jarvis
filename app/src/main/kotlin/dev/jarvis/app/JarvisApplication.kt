package dev.jarvis.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context

/**
 * Composition root.
 *
 * Jarvis uses hand-wired dependency injection rather than an annotation processor: the
 * object graph must be constructible from a Service or BroadcastReceiver (the assistant
 * spends most of its life outside the UI), and avoiding kapt/ksp keeps the build
 * reproducible with no code generation step. Everything the assistant needs is reachable
 * from [graph].
 */
class JarvisApplication : Application() {

    /** Built lazily so instrumentation tests can substitute their own. */
    val graph: JarvisGraph by lazy { JarvisGraph.create(this) }

    override fun onCreate() {
        super.onCreate()
        instance = this
        createNotificationChannels()
    }

    /**
     * Channels are created eagerly and with user-facing names.
     *
     * On Android 8+ a notification posted to a channel that was never created is dropped
     * silently, with no exception and no log the user would ever see - which would make
     * reminders and automation results simply vanish. minSdk is 26, so channels are
     * unconditional here.
     */
    private fun createNotificationChannels() {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
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

    companion object {
        // Channel ids are a persisted contract with the user's notification settings:
        // changing one orphans their chosen importance/sound/badge for that channel.
        const val CHANNEL_ASSISTANT = "jarvis.assistant"
        const val CHANNEL_REMINDERS = "jarvis.reminders"
        const val CHANNEL_AUTOMATION = "jarvis.automation"

        @Volatile
        private var instance: JarvisApplication? = null

        /**
         * Access point for components the framework constructs itself (services,
         * receivers), which cannot receive constructor injection.
         */
        fun get(context: Context): JarvisApplication =
            instance
                ?: (context.applicationContext as JarvisApplication).also { instance = it }
    }
}
