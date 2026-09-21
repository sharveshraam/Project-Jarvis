package dev.jarvis.core.ports

import dev.jarvis.core.model.ScreenAction
import dev.jarvis.core.model.ScreenActionResult
import dev.jarvis.core.model.ScreenSnapshot

/**
 * The screen agent port - the boundary between the assistant's reasoning and Android's
 * accessibility framework.
 *
 * Two design rules follow from the requirements:
 *
 *  1. It speaks in [ScreenSnapshot] and [ScreenActionResult], never in
 *     `AccessibilityNodeInfo`. Everything above this line is platform-independent and can be
 *     unit-tested against a synthetic screen.
 *  2. It reports failures instead of throwing, and distinguishes "the platform refused"
 *     ([ScreenActionResult.Failed]) from "this is not available at all"
 *     ([ScreenActionResult.Unavailable]). The agent loop needs that distinction to decide
 *     whether retrying is worthwhile.
 *
 * Implemented on Android by an AccessibilityService. When no service is connected, [isConnected]
 * is false and every call returns Unavailable - the capability catalogue then reports
 * PERMISSION_REQUIRED rather than the assistant failing mid-task.
 */
interface ScreenAgentPort {

    /** True when an accessibility service is connected and able to observe and act. */
    val isConnected: Boolean

    /**
     * Reads the current screen.
     *
     * Returns null when no window is accessible - which includes the secure lock screen, where
     * Android deliberately exposes nothing. Callers must treat null as "cannot see", never as
     * "empty screen".
     *
     * [maxNodes] caps the snapshot. A content-heavy screen can hold thousands of nodes; the cap
     * is a memory guard, and a truncated snapshot says so via [ScreenSnapshot.truncated].
     */
    fun observe(maxNodes: Int = 1200): ScreenSnapshot?

    /**
     * Performs one action.
     *
     * [nodeId] identifies the target within the most recent snapshot. [text] is used by
     * SET_TEXT. Actions that do not need a target (BACK, HOME, RECENTS, NOTIFICATIONS) ignore it.
     */
    fun perform(
        action: ScreenAction,
        nodeId: String? = null,
        text: String? = null,
    ): ScreenActionResult

    /**
     * Re-reads the screen after an action, for the VERIFY phase of the agent loop.
     *
     * Separated from [observe] so an adapter can apply a short settle delay, because a
     * transition animation means an immediate re-read often captures the old screen - which
     * would make verification report a false failure.
     */
    fun observeAfterAction(settleMillis: Long = 350L, maxNodes: Int = 1200): ScreenSnapshot?

    /** Package name of the app currently in the foreground, when it can be determined. */
    fun foregroundPackage(): String?

    /**
     * True when the device is on the secure lock screen.
     *
     * Used to refuse sensitive work rather than attempt it. This is a refusal mechanism, not a
     * bypass: nothing here can unlock the device.
     */
    fun isDeviceLocked(): Boolean
}
