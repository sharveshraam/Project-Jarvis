package dev.jarvis.core.model

/**
 * Tasks, reminders, alarms, timers and notes.
 *
 * These are deliberately separate from memory: a task has a lifecycle and a due time, a
 * memory does not. "TASK MEMORY" in the requirements maps onto this model being *indexed*
 * into memory, not onto storing tasks as free text.
 */

enum class TaskStatus {
    OPEN,
    IN_PROGRESS,
    BLOCKED,
    DONE,
    CANCELLED,
}

enum class Priority { LOW, NORMAL, HIGH, URGENT }

/** What kind of time-based thing this is; each maps to a different Android mechanism. */
enum class ScheduleKind {
    /** No time attached. */
    NONE,

    /** One-shot notification at an exact time (AlarmManager, needs exact-alarm permission). */
    REMINDER,

    /** One-shot alarm that should ring even in Doze (AlarmManager setAlarmClock). */
    ALARM,

    /** Countdown from now (foreground timer, survives screen off). */
    TIMER,

    /** Repeats on a schedule (JobScheduler / rescheduled AlarmManager). */
    RECURRING,
}

data class Recurrence(
    val frequency: Frequency,
    /** 0=Monday .. 6=Sunday, matching ClockPort.localDayOfWeek. */
    val daysOfWeek: Set<Int> = emptySet(),
    val hourOfDay: Int? = null,
    val minute: Int = 0,
    /**
     * Interval for MINUTELY/HOURLY rules, e.g. "every 30 minutes".
     *
     * Without this, an interval rule would have to collapse onto "every minute" or "every
     * hour", which would fire far more often than the user asked.
     */
    val intervalMillis: Long? = null,
) {
    enum class Frequency { MINUTELY, HOURLY, DAILY, WEEKLY, MONTHLY }
}

data class TaskItem(
    val id: String,
    val title: String,
    val notes: String? = null,
    val status: TaskStatus = TaskStatus.OPEN,
    val priority: Priority = Priority.NORMAL,
    val createdAt: Long,
    val updatedAt: Long,
    val dueAt: Long? = null,
    val completedAt: Long? = null,
    val schedule: ScheduleKind = ScheduleKind.NONE,
    val recurrence: Recurrence? = null,
    val project: String? = null,
    val tags: List<String> = emptyList(),
    /** Set when the user asked for this to also be a long-term memory. */
    val linkedMemoryId: String? = null,
) {
    val isDone: Boolean get() = status == TaskStatus.DONE || status == TaskStatus.CANCELLED
    val isOverdue: Boolean get() = !isDone && dueAt != null && dueAt < updatedAt
}

/** A note is free text the user wants kept, distinct from a memory (which is about the user). */
data class Note(
    val id: String,
    val title: String,
    val body: String,
    val createdAt: Long,
    val updatedAt: Long,
    val tags: List<String> = emptyList(),
)

/**
 * A timer/alarm/reminder that is actually armed on the device.
 *
 * The core decides *what* to schedule; the Android adapter decides *how* (AlarmManager vs
 * JobScheduler vs a foreground countdown) and reports back what it managed to do, including
 * whether exact timing was denied.
 */
data class ScheduledEvent(
    val id: String,
    val taskId: String?,
    val kind: ScheduleKind,
    val fireAt: Long,
    val message: String,
    val createdAt: Long,
    val recurrence: Recurrence? = null,
    val exact: Boolean = true,
) {
    /**
     * True when the OS refused exact scheduling and this fell back to an inexact window.
     * Surfaced to the user rather than hidden, because "remind me at 7" firing at 7:12
     * silently would be worse than saying so.
     */
    val degradedPrecision: Boolean get() = kind != ScheduleKind.TIMER && !exact
}
