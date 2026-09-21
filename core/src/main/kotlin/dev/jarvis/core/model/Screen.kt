package dev.jarvis.core.model

/**
 * The screen model.
 *
 * This is the *semantic* representation of a screen, deliberately independent of Android's
 * `AccessibilityNodeInfo`. The accessibility adapter translates the platform tree into this,
 * and everything above it - matching, planning, verification - operates on this model only.
 * That is what makes the screen agent replaceable, and what makes it testable without a
 * device: a fake screen can be constructed in a unit test and the selector exercised on it.
 *
 * Matching is semantic, never coordinate-based. Coordinates exist here only as a last-resort
 * fallback for tap injection and for reporting, and the selector explicitly prefers text,
 * content descriptions, roles, resource ids and structural position.
 */

/** What a node *is*, semantically - mapped from className plus behaviour flags. */
enum class UiRole {
    BUTTON,
    TEXT,
    HEADING,
    EDITABLE,
    LIST,
    LIST_ITEM,
    IMAGE,
    LINK,
    CHECKBOX,
    SWITCH,
    RADIO,
    TAB,
    SCROLLABLE,
    CONTAINER,
    MEDIA_CONTROL,
    UNKNOWN,
}

data class Bounds(
    val left: Int,
    val top: Int,
    val right: Int,
    val bottom: Int,
) {
    val width: Int get() = (right - left).coerceAtLeast(0)
    val height: Int get() = (bottom - top).coerceAtLeast(0)
    val centerX: Int get() = left + width / 2
    val centerY: Int get() = top + height / 2
    val area: Int get() = width * height
    val isOnScreen: Boolean get() = width > 0 && height > 0

    /** Vertical ordering key; ties broken by horizontal position (reading order). */
    val readingOrderKey: Long get() = top.toLong() * 100_000L + left

    fun contains(x: Int, y: Int): Boolean = x in left..right && y in top..bottom
}

/**
 * One node of the screen tree.
 *
 * [label] is the node's best available human-readable label, resolved by the adapter in this
 * order of preference: text, content description, hint text, resource id name, class simple
 * name. Everything downstream matches against [label] and the individual fields.
 */
data class UiNode(
    val nodeId: String,
    val role: UiRole,
    val label: String,
    val text: String = "",
    val contentDescription: String = "",
    val hintText: String = "",
    val viewIdResourceName: String? = null,
    val className: String? = null,
    val packageName: String? = null,
    val clickable: Boolean = false,
    val longClickable: Boolean = false,
    val scrollable: Boolean = false,
    val editable: Boolean = false,
    val checkable: Boolean = false,
    val checked: Boolean? = null,
    val enabled: Boolean = true,
    val selected: Boolean = false,
    val focused: Boolean = false,
    val visible: Boolean = true,
    val bounds: Bounds = Bounds(0, 0, 0, 0),
    val depth: Int = 0,
    val indexInParent: Int = 0,
    /** Ids of children, so the tree can be flattened without recursion limits. */
    val childIds: List<String> = emptyList(),
    val parentId: String? = null,
) {
    /** All textual evidence this node offers, in descending order of usefulness. */
    fun matchableText(): List<String> = listOfNotNull(
        text.takeIf { it.isNotBlank() },
        contentDescription.takeIf { it.isNotBlank() },
        hintText.takeIf { it.isNotBlank() },
        viewIdResourceName?.substringAfterLast('/')?.replace('_', ' ')?.takeIf { it.isNotBlank() },
        label.takeIf { it.isNotBlank() },
    ).distinct()

    /** The id-without-package, e.g. "com.google.android.youtube" + "play_button" -> "play_button". */
    val simpleId: String? get() = viewIdResourceName?.substringAfterLast('/')

    val isInteractive: Boolean get() = clickable || longClickable || editable || scrollable || checkable
}

/**
 * A structurally detected list: a set of sibling nodes that look alike.
 *
 * This is how "the second recommended video" works without any app-specific selector. The
 * detector looks for a parent whose children share a class/structure signature and repeats
 * at least [minItems] times, then treats those children as ordered items. It is heuristic
 * and is reported as heuristic - when detection is ambiguous the assistant says so rather
 * than guessing silently.
 */
data class DetectedList(
    val parentNodeId: String,
    val itemNodeIds: List<String>,
    /** Structural signature shared by the items, used for confidence. */
    val signature: String,
    val confidence: Double,
    /** True when the container itself can scroll. */
    val scrollable: Boolean,
    /** A short human label if one was found near the list ("Up next", "Recommended"). */
    val headingHint: String? = null,
) {
    val size: Int get() = itemNodeIds.size
}

/**
 * A snapshot of the screen at a moment in time.
 *
 * [truncated] is important: the accessibility tree on a content-heavy screen can contain
 * thousands of nodes, and a snapshot that was cut down for size must say so, because
 * "the item you asked for is not on this screen" may really mean "it is below the fold".
 */
data class ScreenSnapshot(
    val packageName: String?,
    val activityName: String?,
    val capturedAt: Long,
    val nodes: Map<String, UiNode>,
    val rootNodeId: String?,
    val screenTitle: String? = null,
    val truncated: Boolean = false,
    val totalNodeCount: Int = nodes.size,
    val screenWidth: Int = 0,
    val screenHeight: Int = 0,
) {
    /** All nodes in reading order. */
    val ordered: List<UiNode>
        get() = nodes.values
            .filter { it.visible }
            .sortedBy { it.bounds.readingOrderKey }

    fun node(id: String): UiNode? = nodes[id]

    fun childrenOf(id: String): List<UiNode> =
        nodes[id]?.childIds?.mapNotNull { nodes[it] } ?: emptyList()

    fun withRole(role: UiRole): List<UiNode> = ordered.filter { it.role == role }

    fun clickable(): List<UiNode> = ordered.filter { it.clickable && it.enabled }

    fun editable(): List<UiNode> = ordered.filter { it.editable && it.enabled }

    fun scrollable(): List<UiNode> = ordered.filter { it.scrollable }

    /** Best guess at a human-readable title for the current screen. */
    fun describe(): String {
        val app = packageName?.substringAfterLast('.') ?: "unknown app"
        val title = screenTitle?.takeIf { it.isNotBlank() }
        return if (title != null) "$app: $title" else app
    }

    companion object {
        val EMPTY = ScreenSnapshot(
            packageName = null,
            activityName = null,
            capturedAt = 0L,
            nodes = emptyMap(),
            rootNodeId = null,
        )
    }
}

/** An action the screen agent can perform on a node or the whole screen. */
enum class ScreenAction {
    TAP,
    LONG_PRESS,
    SET_TEXT,
    CLEAR_TEXT,
    SCROLL_FORWARD,
    SCROLL_BACKWARD,
    SCROLL_TO_NODE,
    BACK,
    HOME,
    RECENTS,
    NOTIFICATIONS,
    QUICK_SETTINGS,
    FOCUS,
    COPY_TEXT,
}

/** Outcome of a single screen action, reported by the adapter. */
sealed class ScreenActionResult {
    data class Success(val action: ScreenAction, val nodeId: String? = null, val detail: String = "") :
        ScreenActionResult()

    /** The platform refused, e.g. the node disappeared between observation and action. */
    data class Failed(val action: ScreenAction, val reason: String, val retryable: Boolean = true) :
        ScreenActionResult()

    /** The capability is not available: no accessibility permission, locked device, etc. */
    data class Unavailable(val action: ScreenAction, val reason: String) : ScreenActionResult()
}
