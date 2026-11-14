package dev.jarvis.core.model

/**
 * Automation model: TRIGGER -> CONDITION -> ACTION.
 *
 * Every rule is inspectable, editable, disable-able and deletable by the user; nothing here
 * is implicit or hidden. A rule that fires a consequential action is marked
 * [requiresConfirmation] and will not run unattended.
 */

enum class TriggerType(val label: String) {
    TIME("At a specific time"),
    RECURRING_TIME("At a repeating time"),
    BATTERY_LOW("When battery falls below a level"),
    BATTERY_OK("When battery rises above a level"),
    CHARGER_CONNECTED("When a charger is connected"),
    CHARGER_DISCONNECTED("When a charger is disconnected"),
    HEADSET_CONNECTED("When headphones are connected"),
    HEADSET_DISCONNECTED("When headphones are disconnected"),
    APP_OPENED("When an app is opened"),
    APP_CLOSED("When an app is left"),
    NOTIFICATION_RECEIVED("When a notification arrives"),
    SCREEN_ON("When the screen turns on"),
    SCREEN_OFF("When the screen turns off"),
    LOCATION_ARRIVE("When arriving at a saved place"),
    LOCATION_LEAVE("When leaving a saved place"),
    TASK_COMPLETED("When a task is completed"),
    DEVICE_BOOTED("After the device restarts"),
    MANUAL("When triggered manually"),
}

enum class ConditionType(val label: String) {
    ALWAYS("Always"),
    TIME_WINDOW("Only within a time window"),
    DAY_OF_WEEK("Only on certain days"),
    BATTERY_ABOVE("Only above a battery level"),
    BATTERY_BELOW("Only below a battery level"),
    CHARGING("Only while charging"),
    NOT_CHARGING("Only while not charging"),
    APP_IN_FOREGROUND("Only when an app is open"),
    APP_NOT_IN_FOREGROUND("Only when an app is not open"),
    ONLINE("Only when online"),
    OFFLINE("Only when offline"),
    HEADSET_CONNECTED("Only when headphones are connected"),
    SCREEN_ON("Only when the screen is on"),
    DEVICE_UNLOCKED("Only when the device is unlocked"),
    NOT_RECENTLY_FIRED("Only if it has not fired recently"),
}

/**
 * A condition's parameters are strings so a rule can be persisted, exported and edited
 * without the core knowing about Android's Bundle/Intent types.
 */
data class AutomationCondition(
    val type: ConditionType,
    val params: Map<String, String> = emptyMap(),
) {
    fun param(key: String): String? = params[key]
    fun paramInt(key: String): Int? = params[key]?.toIntOrNull()
}

data class AutomationTrigger(
    val type: TriggerType,
    val params: Map<String, String> = emptyMap(),
    val recurrence: Recurrence? = null,
) {
    fun param(key: String): String? = params[key]
    fun paramInt(key: String): Int? = params[key]?.toIntOrNull()
}

/**
 * An action is expressed as a *tool name plus arguments*, which is what makes automations
 * and the conversational agent share one execution path, one permission model and one risk
 * model. There is no separate automation action language.
 */
data class AutomationAction(
    val tool: String,
    val args: Map<String, String> = emptyMap(),
    /** Free-text description shown in the rule editor. */
    val description: String? = null,
)

data class AutomationRule(
    val id: String,
    val name: String,
    val trigger: AutomationTrigger,
    val conditions: List<AutomationCondition> = emptyList(),
    val actions: List<AutomationAction> = emptyList(),
    val enabled: Boolean = true,
    val createdAt: Long = 0L,
    val updatedAt: Long = 0L,
    val lastFiredAt: Long? = null,
    val fireCount: Int = 0,
    val lastError: String? = null,
    /** Consequential actions are never executed unattended without this being false. */
    val requiresConfirmation: Boolean = true,
    /** Suppresses repeat firing inside this window (millis). */
    val cooldownMillis: Long = 0L,
)

/**
 * A snapshot of the device state a rule is evaluated against.
 *
 * Passed in from the Android adapter, so the rule engine itself is pure and testable: given
 * an event and a world state, which rules should fire? That is the whole automation logic
 * and it can be unit-tested without a device.
 */
data class WorldState(
    val nowMillis: Long,
    val batteryPercent: Int = -1,
    val isCharging: Boolean = false,
    val headsetConnected: Boolean = false,
    val foregroundPackage: String? = null,
    val isOnline: Boolean = false,
    val dayOfWeek: Int = 0,
    val hourOfDay: Int = 0,
    val minuteOfHour: Int = 0,
    val screenOn: Boolean = false,
    val deviceUnlocked: Boolean = false,
)

/** An event that may cause rules to fire. */
data class TriggerEvent(
    val type: TriggerType,
    val atMillis: Long,
    val params: Map<String, String> = emptyMap(),
)

/** What the engine decided. Actions are NOT executed by the engine - it only decides. */
data class AutomationDecision(
    val rule: AutomationRule,
    val actions: List<AutomationAction>,
    val needsConfirmation: Boolean,
    val reason: String,
)

/** Why a rule did not fire; kept so the rule editor can explain itself. */
data class RuleEvaluation(
    val rule: AutomationRule,
    val triggered: Boolean,
    val conditionsMet: Boolean,
    val blockedBy: AutomationCondition? = null,
    val explanation: String,
)
