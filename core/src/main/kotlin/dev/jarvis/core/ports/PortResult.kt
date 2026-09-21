package dev.jarvis.core.ports

/**
 * The result type every port returns.
 *
 * A port never throws to signal "the platform said no", and never returns a bare Boolean that
 * loses the reason. Requirement §16 is that the assistant must never claim an action succeeded
 * when it did not - which is only enforceable if failure carries a reason that can be reported
 * to the user verbatim.
 */
sealed class PortResult {
    /** The platform accepted the request. [detail] is safe to show the user. */
    data class Ok(
        val detail: String = "",
        val data: Map<String, String> = emptyMap(),
    ) : PortResult()

    /**
     * The request was attempted and did not work.
     *
     * [recoverable] drives the agent loop: a recoverable failure (the node vanished because the
     * list scrolled) is worth re-observing and retrying, an unrecoverable one is not.
     */
    data class Failed(
        val reason: String,
        val recoverable: Boolean = true,
    ) : PortResult()

    /**
     * Not attempted, because something the user controls is missing: a permission, a Settings
     * toggle, connectivity, or an uninstalled model. [capabilityId] points at the catalogue
     * entry that explains it and offers the remedy.
     */
    data class Denied(
        val reason: String,
        val capabilityId: String? = null,
    ) : PortResult()

    val isOk: Boolean get() = this is Ok
}
