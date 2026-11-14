using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Models;
using Jarvis.Core.Modes;
using Jarvis.Core.Security;
using Jarvis.Core.Text;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Agent;

public sealed record ToolActivity(string ToolName, ToolCategory Category, bool Started, ToolResult? Result);

/// <summary>
/// The only path by which a model-requested action reaches the computer:
/// look up the tool → check the mode → validate arguments → assess this call → check permissions
/// (asking the user when required) → execute with a timeout → audit → return the actual outcome.
/// </summary>
public sealed class ToolExecutor
{
    private readonly ToolRegistry _registry;
    private readonly ISettingsStore _settings;
    private readonly IApprovalService _approvals;
    private readonly IAuditLog _audit;
    private readonly ILogger<ToolExecutor> _log;

    public ToolExecutor(ToolRegistry registry, ISettingsStore settings, IApprovalService approvals, IAuditLog audit, ILogger<ToolExecutor> log)
    {
        _registry = registry;
        _settings = settings;
        _approvals = approvals;
        _audit = audit;
        _log = log;
    }

    public ToolRegistry Registry => _registry;

    /// <summary>Raised when a tool starts and finishes, so the UI can show what JARVIS is doing.</summary>
    public event Action<ToolActivity>? Activity;

    /// <summary>The tools Gemini is told about in a mode: the mode's categories minus anything the user blocked.</summary>
    public IReadOnlyList<ToolDefinition> DefinitionsFor(JarvisMode mode)
    {
        var definition = ModeCatalog.Get(mode);
        var settings = _settings.Current;
        return _registry.All
            .Select(t => t.Definition)
            .Where(d => definition.Allows(d.Category))
            .Where(d => !(settings.ToolPermissions.TryGetValue(d.Name, out var level) && level == PermissionLevel.Deny))
            .ToList();
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonObject args, ToolContext context, CancellationToken ct)
    {
        if (!_registry.TryGet(name, out var tool))
            return Audit(name, "unknown", "rejected", ToolResult.Fail($"There is no tool named '{name}'."), "");

        var definition = tool.Definition;
        if (!context.BypassModeFilter && !ModeCatalog.Get(context.Mode).Allows(definition.Category))
        {
            var modes = string.Join(", ", ModeCatalog.All.Where(m => m.Allows(definition.Category)).Select(m => m.DisplayName));
            return Audit(name, definition.DefaultRisk.ToString(), "mode-blocked",
                ToolResult.Refused($"'{name}' is not available in {ModeCatalog.Get(context.Mode).DisplayName} mode. It is available in: {modes}."), "");
        }

        var errors = SchemaValidator.Validate(definition, args);
        if (errors.Count > 0)
            return Audit(name, definition.DefaultRisk.ToString(), "invalid", ToolResult.Fail("Invalid arguments: " + string.Join(" ", errors)), "");

        ToolAssessment assessment;
        try
        {
            assessment = tool.Assess(args, context);
        }
        catch (ToolArgumentException ex)
        {
            return Audit(name, definition.DefaultRisk.ToString(), "invalid", ToolResult.Fail(ex.Message), "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Audit(name, definition.DefaultRisk.ToString(), "invalid", ToolResult.Fail(ex.Message), "");
        }

        var risk = assessment.Risk.ToString();
        var decision = PermissionPolicy.Decide(definition, assessment.Risk, _settings.Current);
        if (decision == PermissionLevel.Deny)
            return Audit(name, risk, "blocked", ToolResult.Refused($"'{name}' is blocked in Settings › Permissions."), assessment.Summary);

        var decisionText = "allowed";
        if (decision == PermissionLevel.Ask)
        {
            context.ApprovalStateChanged?.Invoke(true);
            ApprovalResult approval;
            try
            {
                approval = await _approvals.RequestAsync(
                    new ApprovalRequest(name, assessment.Risk, assessment.Summary, assessment.Details, context.IsBackground, context.TaskId), ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                context.ApprovalStateChanged?.Invoke(false);
            }

            if (!approval.Approved)
                return Audit(name, risk, "declined", ToolResult.Refused("You declined this action, so nothing was changed."), assessment.Summary);

            decisionText = "approved";
            if (approval.RememberForTool && assessment.Risk == ToolRisk.Routine)
            {
                var updated = _settings.Current.Clone();
                updated.ToolPermissions[name] = PermissionLevel.Allow;
                _settings.Save(updated);
            }
        }

        Activity?.Invoke(new ToolActivity(name, definition.Category, true, null));
        ToolResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(definition.Timeout);
            try
            {
                result = await tool.ExecuteAsync(args, context, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Audit(name, risk, decisionText, ToolResult.Fail("Cancelled."), assessment.Summary);
                Activity?.Invoke(new ToolActivity(name, definition.Category, false, null));
                throw;
            }
            catch (OperationCanceledException)
            {
                result = ToolResult.Fail($"'{name}' did not finish within {definition.Timeout.TotalSeconds:0} seconds and was stopped.");
            }
            catch (ToolArgumentException ex)
            {
                result = ToolResult.Fail(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // File-system errors already carry a user-presentable explanation.
                result = ToolResult.Fail(SecretDetector.Redact(ex.Message));
            }
            catch (Exception ex)
            {
                // A failing tool must never take the assistant down; report the real reason instead.
                _log.LogWarning(ex, "Tool {Tool} failed.", name);
                result = ToolResult.Fail($"'{name}' failed: {SecretDetector.Redact(ex.Message)}");
            }
        }

        Audit(name, risk, decisionText, result, assessment.Summary);
        Activity?.Invoke(new ToolActivity(name, definition.Category, false, result));
        return result;
    }

    private ToolResult Audit(string tool, string risk, string decision, ToolResult result, string summary)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(summary) ? result.Message : summary + " → " + result.Message;
            _audit.Record(tool, risk, decision, result.Success, Relevance.Truncate(SecretDetector.Redact(text), 500));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audit entry could not be written.");
        }
        _log.LogInformation("Tool {Tool} [{Risk}] {Decision}: success={Success} verified={Verified}", tool, risk, decision, result.Success, result.Verified);
        return result;
    }
}
