using System.Text.Json.Nodes;
using Jarvis.Agent;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;

namespace Jarvis.Tests;

/// <summary>The gate every model-requested action passes through.</summary>
public class ToolExecutorTests
{
    private readonly ExecutorHarness _harness = new ExecutorHarness();

    [Fact]
    public async Task Unknown_tools_are_reported_not_invented()
    {
        var result = await _harness.Run("teleport");

        Assert.False(result.Success);
        Assert.Contains("no tool named 'teleport'", result.Message);
        Assert.Equal("rejected", Assert.Single(_harness.Audit.Entries).Decision);
    }

    [Fact]
    public async Task Safe_tools_run_without_asking()
    {
        _harness.Add("read_thing", ToolRisk.Safe);

        var result = await _harness.Run("read_thing");

        Assert.True(result.Success);
        Assert.Equal(1, _harness.Runs);
        Assert.Empty(_harness.Approvals.Requests);
        Assert.Equal("allowed", Assert.Single(_harness.Audit.Entries).Decision);
    }

    [Fact]
    public async Task Consequential_tools_do_nothing_when_the_user_declines()
    {
        _harness.Add("delete_thing", ToolRisk.Consequential);

        var result = await _harness.Run("delete_thing");

        Assert.False(result.Success);
        Assert.True(result.Denied);
        Assert.Equal(0, _harness.Runs);
        Assert.Single(_harness.Approvals.Requests);
        Assert.Equal("declined", Assert.Single(_harness.Audit.Entries).Decision);
    }

    [Fact]
    public async Task Consequential_tools_run_once_approved()
    {
        _harness.Add("delete_thing", ToolRisk.Consequential);
        _harness.Approvals.Answer = new ApprovalResult(true);

        var result = await _harness.Run("delete_thing");

        Assert.True(result.Success);
        Assert.Equal(1, _harness.Runs);
        Assert.Equal("approved", Assert.Single(_harness.Audit.Entries).Decision);
    }

    [Fact]
    public async Task Consequential_tools_ask_even_when_settings_say_allow()
    {
        _harness.Add("delete_thing", ToolRisk.Consequential);
        var settings = _harness.Settings.Current.Clone();
        settings.ToolPermissions["delete_thing"] = PermissionLevel.Allow;
        settings.TrustRoutineActions = true;
        _harness.Settings.Save(settings);

        await _harness.Run("delete_thing");

        Assert.Single(_harness.Approvals.Requests);
        Assert.Equal(0, _harness.Runs);
    }

    [Fact]
    public async Task Approval_of_a_consequential_tool_is_never_remembered()
    {
        _harness.Add("delete_thing", ToolRisk.Consequential);
        _harness.Approvals.Answer = new ApprovalResult(true, RememberForTool: true);

        await _harness.Run("delete_thing");
        await _harness.Run("delete_thing");

        Assert.Equal(2, _harness.Approvals.Requests.Count);
        Assert.False(_harness.Approvals.Requests[0].CanRemember);
        Assert.False(_harness.Settings.Current.ToolPermissions.ContainsKey("delete_thing"));
    }

    [Fact]
    public async Task Routine_tools_follow_the_trust_setting()
    {
        _harness.Add("move_thing", ToolRisk.Routine);

        await _harness.Run("move_thing");
        Assert.Empty(_harness.Approvals.Requests);
        Assert.Equal(1, _harness.Runs);

        var settings = _harness.Settings.Current.Clone();
        settings.TrustRoutineActions = false;
        _harness.Settings.Save(settings);

        var result = await _harness.Run("move_thing");
        Assert.True(result.Denied);
        Assert.Single(_harness.Approvals.Requests);
        Assert.Equal(1, _harness.Runs);
    }

    [Fact]
    public async Task Always_allow_is_remembered_for_routine_tools()
    {
        _harness.Add("move_thing", ToolRisk.Routine);
        var settings = _harness.Settings.Current.Clone();
        settings.TrustRoutineActions = false;
        _harness.Settings.Save(settings);
        _harness.Approvals.Answer = new ApprovalResult(true, RememberForTool: true);

        await _harness.Run("move_thing");
        await _harness.Run("move_thing");

        Assert.Single(_harness.Approvals.Requests);
        Assert.True(_harness.Approvals.Requests[0].CanRemember);
        Assert.Equal(2, _harness.Runs);
        Assert.Equal(PermissionLevel.Allow, _harness.Settings.Current.ToolPermissions["move_thing"]);
    }

    [Fact]
    public async Task Blocked_tools_never_run_and_never_ask()
    {
        _harness.Add("read_thing", ToolRisk.Safe);
        var settings = _harness.Settings.Current.Clone();
        settings.ToolPermissions["read_thing"] = PermissionLevel.Deny;
        _harness.Settings.Save(settings);

        var result = await _harness.Run("read_thing");

        Assert.True(result.Denied);
        Assert.Equal(0, _harness.Runs);
        Assert.Empty(_harness.Approvals.Requests);
        Assert.Equal("blocked", Assert.Single(_harness.Audit.Entries).Decision);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_anything_runs()
    {
        _harness.Add("read_thing", ToolRisk.Safe);

        var result = await _harness.Run("read_thing", new JsonObject { ["bogus"] = 1 });

        Assert.False(result.Success);
        Assert.Contains("Unknown argument 'bogus'.", result.Message);
        Assert.Equal(0, _harness.Runs);
        Assert.Equal("invalid", Assert.Single(_harness.Audit.Entries).Decision);
    }

    [Fact]
    public async Task Tools_outside_the_active_mode_are_refused_with_guidance()
    {
        _harness.Add("git_thing", ToolRisk.Safe, ToolCategory.Development);

        var blocked = await _harness.Run("git_thing", null, new ToolContext { Mode = JarvisMode.Study });
        Assert.True(blocked.Denied);
        Assert.Contains("Study mode", blocked.Message);
        Assert.Contains("Development", blocked.Message);
        Assert.Equal(0, _harness.Runs);

        var allowed = await _harness.Run("git_thing", null, new ToolContext { Mode = JarvisMode.Development });
        Assert.True(allowed.Success);

        var direct = await _harness.Run("git_thing", null, new ToolContext { Mode = JarvisMode.Study, BypassModeFilter = true });
        Assert.True(direct.Success);
    }

    [Fact]
    public void Each_mode_advertises_a_different_tool_set()
    {
        _harness.Add("general_thing", ToolRisk.Safe, ToolCategory.General);
        _harness.Add("git_thing", ToolRisk.Safe, ToolCategory.Development);
        _harness.Add("file_thing", ToolRisk.Routine, ToolCategory.Files);

        var study = _harness.Executor.DefinitionsFor(JarvisMode.Study).Select(d => d.Name).ToList();
        var development = _harness.Executor.DefinitionsFor(JarvisMode.Development).Select(d => d.Name).ToList();
        var focus = _harness.Executor.DefinitionsFor(JarvisMode.Focus).Select(d => d.Name).ToList();

        Assert.Contains("general_thing", study);
        Assert.DoesNotContain("git_thing", study);
        Assert.Contains("git_thing", development);
        Assert.DoesNotContain("file_thing", focus);
        Assert.Contains("general_thing", focus);
    }

    [Fact]
    public void Blocked_tools_are_not_advertised_to_the_model()
    {
        _harness.Add("general_thing", ToolRisk.Safe, ToolCategory.General);
        var settings = _harness.Settings.Current.Clone();
        settings.ToolPermissions["general_thing"] = PermissionLevel.Deny;
        _harness.Settings.Save(settings);

        Assert.Empty(_harness.Executor.DefinitionsFor(JarvisMode.Personal));
    }

    [Fact]
    public async Task A_crashing_tool_is_reported_as_a_failure()
    {
        _harness.Add("broken", ToolRisk.Safe, run: (args, ct) => throw new InvalidOperationException("disk on fire"));

        var result = await _harness.Run("broken");

        Assert.False(result.Success);
        Assert.Contains("disk on fire", result.Message);
        var entry = Assert.Single(_harness.Audit.Entries);
        Assert.False(entry.Success);
    }

    [Fact]
    public async Task A_hung_tool_is_stopped_at_its_timeout()
    {
        _harness.Add("slow", ToolRisk.Safe, timeout: TimeSpan.FromMilliseconds(100), run: async (args, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ToolResult.Ok("finished");
        });

        var result = await _harness.Run("slow");

        Assert.False(result.Success);
        Assert.Contains("did not finish", result.Message);
    }

    [Fact]
    public async Task Cancellation_by_the_user_propagates()
    {
        using var cts = new CancellationTokenSource();
        _harness.Add("slow", ToolRisk.Safe, run: async (args, ct) =>
        {
            cts.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ToolResult.Ok("finished");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _harness.Executor.ExecuteAsync("slow", new JsonObject(), new ToolContext(), cts.Token));
    }

    [Fact]
    public async Task Secrets_in_arguments_are_masked_in_the_audit_log()
    {
        _harness.Add("read_thing", ToolRisk.Safe);

        await _harness.Run("read_thing", new JsonObject { ["value"] = "token=abcdef1234567890" });

        var entry = Assert.Single(_harness.Audit.Entries);
        Assert.DoesNotContain("abcdef1234567890", entry.Summary);
    }

    [Fact]
    public async Task Activity_is_raised_when_a_tool_starts_and_finishes()
    {
        _harness.Add("read_thing", ToolRisk.Safe);
        var events = new List<ToolActivity>();
        _harness.Executor.Activity += events.Add;

        await _harness.Run("read_thing");

        Assert.Equal(2, events.Count);
        Assert.True(events[0].Started);
        Assert.False(events[1].Started);
        Assert.NotNull(events[1].Result);
    }

    [Fact]
    public async Task The_approval_prompt_describes_the_exact_call()
    {
        _harness.Add("delete_thing", ToolRisk.Consequential);

        await _harness.Run("delete_thing", new JsonObject { ["value"] = "report.docx" },
            new ToolContext { IsBackground = true, TaskId = "task-7" });

        var request = Assert.Single(_harness.Approvals.Requests);
        Assert.Equal("delete_thing", request.ToolName);
        Assert.Equal(ToolRisk.Consequential, request.Risk);
        Assert.Contains("report.docx", request.Summary);
        Assert.True(request.IsBackground);
        Assert.Equal("task-7", request.TaskId);
    }
}
