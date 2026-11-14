package dev.jarvis.core.capability

/**
 * The honesty layer.
 *
 * Requirement §27 is that the assistant must never fake functionality, and must be explicit
 * about which of these applies to any given capability:
 *
 *   SUPPORTED / PARTIALLY SUPPORTED / ONLINE REQUIRED / PERMISSION REQUIRED /
 *   DEVICE DEPENDENT / NOT POSSIBLE UNDER CURRENT ANDROID RESTRICTIONS
 *
 * So capability status is modelled as data, resolved from the actual state of the device,
 * and surfaced both to the user (Settings > Capabilities) and to the engine, which refuses to
 * claim success for something it could not do.
 */
enum class CapabilityStatus(val label: String) {
    /** Works right now, on this device, with the permissions already granted. */
    SUPPORTED("Supported"),

    /** Works, but with a limitation the user should know about. [Capability.explanation] says what. */
    PARTIALLY_SUPPORTED("Partially supported"),

    /** Needs a permission that is not currently granted. [Capability.remedy] says how to grant it. */
    PERMISSION_REQUIRED("Permission required"),

    /** Needs network connectivity, and the device is offline (or online features are off). */
    ONLINE_REQUIRED("Online required"),

    /** Depends on hardware or an OS version this device does not have. */
    DEVICE_DEPENDENT("Device dependent"),

    /** Needs an on-device model the user has not installed. */
    MODEL_REQUIRED("Model required"),

    /** The user turned it off in Settings. One toggle away from working. */
    DISABLED_BY_USER("Disabled by you"),

    /** Android does not allow this. No workaround is claimed. */
    NOT_POSSIBLE("Not possible on Android"),
}

/** Coarse grouping used by the Capabilities screen. */
enum class CapabilityGroup(val label: String) {
    CONVERSATION("Conversation and memory"),
    SCREEN("Screen understanding and in-app control"),
    VOICE("Voice"),
    PHONE("Phone, media and messaging"),
    SCHEDULING("Reminders, timers and automation"),
    TEACHING("Teaching and knowledge"),
    ONLINE("Online features"),
    SECURITY("Security and privacy"),
}

/** Permissions the assistant may need, named in its own vocabulary rather than Android's. */
enum class PermissionKind(val androidPermission: String?, val label: String) {
    MICROPHONE("android.permission.RECORD_AUDIO", "Microphone"),
    NOTIFICATIONS("android.permission.POST_NOTIFICATIONS", "Notifications"),
    CONTACTS("android.permission.READ_CONTACTS", "Contacts"),
    LOCATION("android.permission.ACCESS_FINE_LOCATION", "Location"),
    EXACT_ALARM("android.permission.SCHEDULE_EXACT_ALARM", "Exact alarms"),
    BLUETOOTH_CONNECT("android.permission.BLUETOOTH_CONNECT", "Nearby devices"),
    /** Not a runtime permission: a Settings toggle the user must flip. */
    ACCESSIBILITY(null, "Accessibility (screen agent)"),
    /** Not a runtime permission: a Settings toggle the user must flip. */
    NOTIFICATION_ACCESS(null, "Notification access"),
    /** Not a runtime permission: user opt-in inside the app. */
    ONLINE_AI(null, "Online AI"),
}

enum class BiometricAvailability {
    AVAILABLE,
    NO_HARDWARE,
    NONE_ENROLLED,
    TEMPORARILY_UNAVAILABLE,
    NOT_SUPPORTED_ON_THIS_API,
}

/**
 * What the assistant asks the platform about itself.
 *
 * Implemented by the Android adapter; faked in tests. Every status decision goes through
 * this, so a capability can never report SUPPORTED on a device where it is not.
 */
interface DeviceCapabilityProbe {
    fun isOnline(): Boolean
    fun accessibilityServiceEnabled(): Boolean
    fun notificationListenerEnabled(): Boolean
    fun hasPermission(kind: PermissionKind): Boolean
    fun exactAlarmsAllowed(): Boolean
    fun hasMicrophoneHardware(): Boolean
    fun hasTtsEngine(): Boolean
    fun hasSpeechRecognizer(): Boolean
    fun biometricAvailability(): BiometricAvailability
    fun deviceLocked(): Boolean
    /** Id of an installed on-device language model, or null when none is installed. */
    fun installedLocalModel(): String?
    fun isIgnoringBatteryOptimizations(): Boolean
    fun androidApiLevel(): Int
}

/**
 * One capability, and how its status is resolved from the device.
 *
 * [resolve] returns the *current* status. The static fields describe the capability in the
 * best case so the UI can explain what it would take to get there.
 */
class Capability(
    val id: String,
    val name: String,
    val group: CapabilityGroup,
    /** Best-case status, i.e. what this is when everything it needs is available. */
    val bestCase: CapabilityStatus,
    val explanation: String,
    /** What the user can do about a non-SUPPORTED status. */
    val remedy: String? = null,
    /** Hard Android limitation, if any. Present => status can never be SUPPORTED. */
    val androidLimitation: String? = null,
    private val resolve: (DeviceCapabilityProbe) -> CapabilityStatus,
) {
    fun status(probe: DeviceCapabilityProbe): CapabilityStatus = resolve(probe)

    fun describe(probe: DeviceCapabilityProbe): CapabilityReport = CapabilityReport(
        capability = this,
        status = status(probe),
    )
}

data class CapabilityReport(
    val capability: Capability,
    val status: CapabilityStatus,
) {
    val id: String get() = capability.id
    val name: String get() = capability.name
    val group: CapabilityGroup get() = capability.group

    /**
     * A sentence the assistant can say out loud. It never claims more than the status
     * allows, and it always says what would change the answer.
     */
    fun spokenExplanation(): String = when (status) {
        CapabilityStatus.SUPPORTED -> "$name works."
        CapabilityStatus.PARTIALLY_SUPPORTED ->
            "$name works, with a limitation: ${capability.explanation}"
        CapabilityStatus.PERMISSION_REQUIRED ->
            "$name needs a permission that is not granted yet." +
                (capability.remedy?.let { " $it" } ?: "")
        CapabilityStatus.ONLINE_REQUIRED ->
            "$name needs an internet connection. Everything else still works offline."
        CapabilityStatus.DEVICE_DEPENDENT ->
            "$name depends on hardware or an Android version this device does not have." +
                (capability.explanation.let { " $it" })
        CapabilityStatus.MODEL_REQUIRED ->
            "$name needs an on-device model that is not installed." +
                (capability.remedy?.let { " $it" } ?: "")
        CapabilityStatus.DISABLED_BY_USER ->
            "$name is turned off in Settings." + (capability.remedy?.let { " $it" } ?: "")
        CapabilityStatus.NOT_POSSIBLE ->
            "$name is not possible on Android." +
                (capability.androidLimitation?.let { " $it" } ?: "")
    }
}
