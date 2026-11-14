package dev.jarvis.core.capability

import dev.jarvis.core.config.AssistantConfig

/**
 * Capability status as the user will actually see it: the device's answer, gated by the
 * user's own configuration.
 *
 * A capability can be technically available and still be off because the user turned it off -
 * and those two cases must not read the same. "Needs a permission" tells you to go to
 * Settings; "Disabled by you" tells you that you chose this and one toggle reverses it.
 */
class CapabilityResolver(
    private val probe: DeviceCapabilityProbe,
    private val config: () -> AssistantConfig,
) {

    /**
     * Which capabilities a given config flag governs. Deliberately explicit rather than
     * inferred, so adding a capability cannot silently escape user control.
     */
    private fun gatedBy(id: String): Boolean {
        val cfg = config()
        return when {
            id.startsWith("memory.") && id != CapabilityCatalog.MEMORY_EXPORT -> !cfg.memoryEnabled
            id == CapabilityCatalog.SEMANTIC_MEMORY_SEARCH -> !cfg.memoryEnabled

            id == CapabilityCatalog.ONLINE_AI -> !cfg.onlineAiEnabled
            id == CapabilityCatalog.WEB_SEARCH -> !cfg.onlineSearchEnabled
            id == CapabilityCatalog.TEACH_ONLINE -> !cfg.onlineAiEnabled
            id == CapabilityCatalog.INGEST_WEB_PAGE -> !cfg.onlineSearchEnabled

            id == CapabilityCatalog.VOICE_HISTORY -> !cfg.voiceHistoryEnabled
            id == CapabilityCatalog.WAKE_WORD -> !cfg.wakeWordEnabled

            id.startsWith("screen.") && id != CapabilityCatalog.READ_SCREEN_WHILE_LOCKED ->
                !cfg.screenAgentEnabled

            id == CapabilityCatalog.AUTOMATION_APP -> !cfg.screenAgentEnabled

            else -> false
        }
    }

    /**
     * Resolved status. User configuration wins over device state: if the user turned a
     * capability off, reporting PERMISSION_REQUIRED for it would be misleading, since granting
     * the permission would not make it work.
     */
    fun status(id: String): CapabilityStatus {
        if (gatedBy(id)) return CapabilityStatus.DISABLED_BY_USER
        return CapabilityCatalog.status(id, probe)
    }

    fun capability(id: String): Capability? = CapabilityCatalog.find(id)

    /** True when the tool behind [id] may actually be attempted right now. */
    fun isAttemptable(id: String): Boolean = when (status(id)) {
        CapabilityStatus.SUPPORTED, CapabilityStatus.PARTIALLY_SUPPORTED -> true
        else -> false
    }

    fun report(id: String): CapabilityReport? = capability(id)?.let {
        CapabilityReport(it, status(id))
    }

    fun all(): List<CapabilityReport> = CapabilityCatalog.all.map {
        CapabilityReport(it, if (gatedBy(it.id)) CapabilityStatus.DISABLED_BY_USER else it.status(probe))
    }

    fun byGroup(group: CapabilityGroup): List<CapabilityReport> = all().filter { it.group == group }

    /** Grouped by what would unblock them, for the Capabilities screen. */
    fun blocked(): Map<CapabilityStatus, List<CapabilityReport>> =
        all().filter { it.status != CapabilityStatus.SUPPORTED }.groupBy { it.status }

    /**
     * A short spoken summary of what is currently unavailable, used when the user asks
     * "what can you do?" - honest, and grouped by remedy so it is actionable.
     */
    fun spokenSummary(nickname: String): String {
        val reports = all()
        val working = reports.count { it.status == CapabilityStatus.SUPPORTED }
        val partial = reports.count { it.status == CapabilityStatus.PARTIALLY_SUPPORTED }
        val blocked = reports.size - working - partial

        val lines = mutableListOf<String>()
        lines += "$working of ${reports.size} capabilities are fully available, " +
            "$partial work with limitations, and $blocked are currently blocked."

        val byStatus = reports.filter {
            it.status != CapabilityStatus.SUPPORTED && it.status != CapabilityStatus.PARTIALLY_SUPPORTED
        }.groupBy { it.status }

        byStatus[CapabilityStatus.PERMISSION_REQUIRED]?.takeIf { it.isNotEmpty() }?.let { group ->
            lines += "Needs a permission: " + group.joinToString(", ") { it.name } + "."
        }
        byStatus[CapabilityStatus.DISABLED_BY_USER]?.takeIf { it.isNotEmpty() }?.let { group ->
            lines += "You have turned off: " + group.joinToString(", ") { it.name } + "."
        }
        byStatus[CapabilityStatus.ONLINE_REQUIRED]?.takeIf { it.isNotEmpty() }?.let { group ->
            lines += "Needs internet: " + group.joinToString(", ") { it.name } + "."
        }
        byStatus[CapabilityStatus.NOT_POSSIBLE]?.takeIf { it.isNotEmpty() }?.let { group ->
            lines += "Android does not allow: " + group.joinToString(", ") { it.name } + "."
            lines += "$nickname will not pretend otherwise."
        }
        return lines.joinToString("\n")
    }
}
