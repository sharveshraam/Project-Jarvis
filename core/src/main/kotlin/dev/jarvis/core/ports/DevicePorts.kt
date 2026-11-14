package dev.jarvis.core.ports

import dev.jarvis.core.capability.BiometricAvailability
import dev.jarvis.core.capability.PermissionKind
import dev.jarvis.core.model.ExtractionQuality

/**
 * Ports onto the device itself: connectivity, status, apps, media, messaging, notifications,
 * scheduling, authentication and documents.
 *
 * Each is small and synchronous unless the platform is genuinely asynchronous (speech,
 * biometric prompts), in which case it takes a callback. The core never blocks on the main
 * thread; the Android adapter decides which thread to run on.
 */

// ------------------------------------------------------------------------ connectivity ==

enum class Connectivity { ONLINE, OFFLINE, METERED, UNKNOWN }

data class NetworkStatus(
    val connectivity: Connectivity,
    val transport: String? = null,
) {
    val isOnline: Boolean get() = connectivity == Connectivity.ONLINE || connectivity == Connectivity.METERED
}

interface NetworkPort {
    fun status(): NetworkStatus
}

// ---------------------------------------------------------------------- device status ==

data class DeviceStatus(
    val batteryPercent: Int,
    val isCharging: Boolean,
    val isPowerSaveMode: Boolean,
    val freeStorageBytes: Long,
    val totalStorageBytes: Long,
    val screenOn: Boolean,
    val deviceLocked: Boolean,
    val headsetConnected: Boolean,
    val wifiConnected: Boolean,
    val mobileDataConnected: Boolean,
    val mediaVolumePercent: Int,
    val ringerMode: String,
    val manufacturer: String,
    val model: String,
    val androidVersion: String,
    val apiLevel: Int,
    val uptimeMillis: Long,
    val isIgnoringBatteryOptimizations: Boolean,
)

interface DeviceStatusPort {
    fun read(): DeviceStatus
}

// ------------------------------------------------------------------------------- apps ==

data class AppInfo(
    val packageName: String,
    val label: String,
    val isSystem: Boolean,
    val isLaunchable: Boolean,
)

interface LauncherPort {
    /**
     * Apps the assistant is allowed to see. Backed by a scoped `<queries>` block for launcher
     * intents, never by QUERY_ALL_PACKAGES.
     */
    fun installedApps(): List<AppInfo>

    fun launch(packageName: String): PortResult

    /**
     * Opens an app's own search if the platform exposes a searchable intent for it.
     *
     * Honest limitation: most apps do not, so this usually falls back to launching the app and
     * driving its search field through the screen agent.
     */
    fun launchSearch(packageName: String, query: String): PortResult

    fun openUrl(url: String): PortResult

    /** Opens the dialer. Pass a number to pre-fill it; the user still presses call. */
    fun openDialer(number: String?): PortResult

    fun uninstallPrompt(packageName: String): PortResult

    fun appSettings(packageName: String): PortResult
}

interface SettingsPort {
    fun open(page: SettingsPage): PortResult
}

enum class SettingsPage(val label: String) {
    ACCESSIBILITY("Accessibility"),
    NOTIFICATION_ACCESS("Notification access"),
    APP_PERMISSIONS("Jarvis permissions"),
    BATTERY_OPTIMIZATION("Battery optimisation"),
    EXACT_ALARMS("Alarms and reminders"),
    DEFAULT_ASSISTANT("Default assistant app"),
    LOCALE("Language and input"),
    SYSTEM_SETTINGS("Settings"),
}

interface PermissionRequestPort {
    fun isGranted(kind: PermissionKind): Boolean

    /** False for permissions that are Settings toggles rather than runtime prompts. */
    fun isRuntimeRequestable(kind: PermissionKind): Boolean

    fun request(kinds: List<PermissionKind>, onResult: (Map<PermissionKind, Boolean>) -> Unit)
}

// ------------------------------------------------------------------------------- media ==

enum class VolumeStream(val label: String) {
    MEDIA("Media"),
    RING("Ring"),
    ALARM("Alarm"),
    NOTIFICATION("Notifications"),
    CALL("Call"),
}

data class MediaState(
    val available: Boolean,
    val isPlaying: Boolean,
    val title: String?,
    val artist: String?,
    val packageName: String?,
    val positionMillis: Long?,
    val durationMillis: Long?,
    /** Present when the app publishes a MediaSession; absent means only on-screen buttons work. */
    val hasTransportControls: Boolean,
)

interface MediaPort {
    fun state(): MediaState
    fun play(): PortResult
    fun pause(): PortResult
    fun stop(): PortResult
    fun next(): PortResult
    fun previous(): PortResult
    fun seekTo(positionMillis: Long): PortResult
    fun seekBy(deltaMillis: Long): PortResult
}

interface VolumePort {
    fun getPercent(stream: VolumeStream): Int
    fun setPercent(stream: VolumeStream, percent: Int): PortResult
    fun adjust(stream: VolumeStream, deltaPercent: Int): PortResult
    fun mute(stream: VolumeStream, muted: Boolean): PortResult
}

// --------------------------------------------------------------------------- messaging ==

data class Contact(
    val id: String,
    val displayName: String,
    val phoneNumbers: List<String> = emptyList(),
    val emails: List<String> = emptyList(),
)

interface ContactPort {
    /** Local lookup. Returns best matches first; an empty list means no permission or no match. */
    fun lookup(query: String, limit: Int = 5): List<Contact>
    fun isAvailable(): Boolean
}

interface MessagingPort {
    /**
     * Hands a composed message to the user's messaging app.
     *
     * This is the honest maximum: Jarvis does not request SEND_SMS, so it cannot send silently.
     * The user presses send in their own app, with the recipient and body already filled in.
     */
    fun composeSms(recipient: String?, body: String): PortResult

    /** Shares text through the system chooser, or straight to [targetPackage] when given. */
    fun shareText(text: String, targetPackage: String? = null): PortResult

    fun shareFile(uri: String, mimeType: String, targetPackage: String? = null): PortResult

    /**
     * Attempt a direct send. Always [PortResult.Denied] in the shipped build, because SEND_SMS
     * is deliberately not requested. Exists so the tool layer can report the limitation
     * precisely instead of failing mysteriously.
     */
    fun sendSmsDirect(recipient: String, body: String): PortResult
}

interface CallPort {
    /** Opens the dialer pre-filled. Jarvis never places a call by itself. */
    fun dial(number: String?): PortResult
}

// ----------------------------------------------------------------------- notifications ==

data class NotificationActionInfo(
    val title: String,
    /** True when the action accepts a typed reply, which is how "reply to the latest" works. */
    val hasRemoteInput: Boolean,
)

data class NotificationInfo(
    val key: String,
    val packageName: String,
    val appLabel: String?,
    val title: String?,
    val text: String?,
    val subtext: String?,
    val postedAt: Long,
    val isOngoing: Boolean,
    val isClearable: Boolean,
    val actions: List<NotificationActionInfo> = emptyList(),
)

interface NotificationPort {
    /** Posts a notification of Jarvis's own (reminders, automation results, timers). */
    fun post(
        channelId: String,
        notificationId: Int,
        title: String,
        body: String,
        importance: NotificationImportance = NotificationImportance.DEFAULT,
        ongoing: Boolean = false,
        actionTitle: String? = null,
        actionPayload: String? = null,
    ): PortResult

    fun cancel(notificationId: Int): PortResult

    /** Reads other apps' notifications. Requires notification access. */
    fun active(): List<NotificationInfo>

    fun dismiss(key: String): PortResult

    /** Triggers a notification's reply action with [text], where the app supports it. */
    fun replyTo(key: String, text: String): PortResult

    /** Triggers a non-reply action by its title, e.g. "Mark as read". */
    fun triggerAction(key: String, actionTitle: String): PortResult

    fun isListenerEnabled(): Boolean
}

enum class NotificationImportance { MIN, LOW, DEFAULT, HIGH }

// -------------------------------------------------------------------------- scheduling ==

interface SchedulerPort {
    /**
     * Arms a reminder.
     *
     * Returns [PortResult.Ok] with `exact=false` in its data when the OS refused exact
     * scheduling and an inexact window was used instead. That degradation is reported to the
     * user rather than hidden, because a reminder that fires twelve minutes late without
     * explanation is worse than one that says it might.
     */
    fun scheduleReminder(event: dev.jarvis.core.model.ScheduledEvent): PortResult

    /**
     * Arms an alarm. Delegates to the system clock where possible, because a third-party alarm
     * can be deferred by Doze and the system clock is exempt.
     */
    fun scheduleAlarm(event: dev.jarvis.core.model.ScheduledEvent): PortResult

    fun cancel(eventId: String): PortResult
    fun cancelAll(): PortResult
    fun scheduled(): List<dev.jarvis.core.model.ScheduledEvent>
    fun exactAlarmsAllowed(): Boolean
}

interface TimerPort {
    fun start(durationMillis: Long, label: String): PortResult
    fun pause(): PortResult
    fun resume(): PortResult
    fun cancel(): PortResult
    fun remainingMillis(): Long?
    val isRunning: Boolean
}

// ---------------------------------------------------------------------- authentication ==

enum class AuthPromptKind {
    BIOMETRIC_OR_DEVICE_CREDENTIAL,
    DEVICE_CREDENTIAL_ONLY,
}

data class AuthRequest(
    val kind: AuthPromptKind,
    val title: String,
    val subtitle: String,
    /** What is being authorised; shown to the user and recorded in the audit trail. */
    val reason: String,
    val capabilityId: String? = null,
)

sealed class AuthResult {
    /** [method] says which credential the platform accepted. */
    data class Granted(val method: String) : AuthResult()
    data class Denied(val reason: String) : AuthResult()
    data object Cancelled : AuthResult()
    data class Unavailable(val reason: String) : AuthResult()
}

interface BiometricPort {
    fun availability(): BiometricAvailability
    /** Asynchronous: the platform owns the prompt, so the result arrives on a callback. */
    fun authenticate(request: AuthRequest, onResult: (AuthResult) -> Unit)
}

// ------------------------------------------------------------------------------- files ==

data class DocumentExtraction(
    val text: String,
    val title: String?,
    val pageCount: Int?,
    val quality: ExtractionQuality,
    val charCount: Int = text.length,
)

interface DocumentReaderPort {
    /**
     * Extracts text from a user-picked URI.
     *
     * A scanned PDF has no text layer and no OCR model is bundled, so this returns
     * [ExtractionQuality.IMAGE_ONLY] with empty text. Callers must report that honestly rather
     * than treating it as "the document said nothing".
     */
    fun extract(uri: String, mimeType: String?): DocumentExtraction
}

interface ClipboardPort {
    fun copy(label: String, text: String): PortResult
    fun read(): String?
}
