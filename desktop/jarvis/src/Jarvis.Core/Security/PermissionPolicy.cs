using Jarvis.Core.Models;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Security;

/// <summary>
/// The single place that decides whether a tool call runs, asks, or is blocked.
/// Consequential actions can never be configured to run silently.
/// </summary>
public static class PermissionPolicy
{
    public static PermissionLevel Decide(ToolDefinition tool, ToolRisk assessedRisk, AppSettings settings)
    {
        var hasOverride = settings.ToolPermissions.TryGetValue(tool.Name, out var configured);
        if (hasOverride && configured == PermissionLevel.Deny) return PermissionLevel.Deny;

        switch (assessedRisk)
        {
            case ToolRisk.Consequential:
                return PermissionLevel.Ask;
            case ToolRisk.Safe:
                return hasOverride && configured == PermissionLevel.Ask ? PermissionLevel.Ask : PermissionLevel.Allow;
            default:
                if (hasOverride) return configured;
                return settings.TrustRoutineActions ? PermissionLevel.Allow : PermissionLevel.Ask;
        }
    }

    /// <summary>Levels the settings UI may offer for a tool, given the highest risk it can carry.</summary>
    public static IReadOnlyList<PermissionLevel> SelectableLevels(ToolRisk defaultRisk)
        => defaultRisk == ToolRisk.Consequential
            ? new[] { PermissionLevel.Ask, PermissionLevel.Deny }
            : new[] { PermissionLevel.Allow, PermissionLevel.Ask, PermissionLevel.Deny };
}
