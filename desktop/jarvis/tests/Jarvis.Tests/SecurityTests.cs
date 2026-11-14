using Jarvis.Core.Models;
using Jarvis.Core.Security;
using Jarvis.Core.Tools;

namespace Jarvis.Tests;

public class PathPolicyTests
{
    private const string Profile = @"C:\Users\tester";
    private readonly AppSettings _settings = new AppSettings();

    private PathPolicy Policy() => new PathPolicy(
        () => _settings, Profile, new[] { @"C:\Windows", @"C:\Program Files", @"C:\Users\tester\.ssh" });

    [Theory]
    [InlineData("Downloads", @"C:\Users\tester\Downloads")]
    [InlineData("my documents folder", @"C:\Users\tester\Documents")]
    [InlineData("desktop", @"C:\Users\tester\Desktop")]
    [InlineData("home", @"C:\Users\tester")]
    [InlineData("~/notes/a.txt", @"C:\Users\tester\notes\a.txt")]
    [InlineData(@"C:\Users\tester\Documents\..\Music", @"C:\Users\tester\Music")]
    public void Friendly_names_and_home_relative_paths_resolve(string input, string expected)
    {
        Assert.True(Policy().TryResolve(input, out var full, out var error), error);
        Assert.Equal(expected, full);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void Relative_or_empty_paths_are_rejected(string? input)
    {
        Assert.False(Policy().TryResolve(input, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData(@"C:\Users\tester\Documents\report.docx")]
    [InlineData(@"C:\Users\tester")]
    [InlineData(@"c:\users\TESTER\Downloads")]
    public void The_users_own_folders_are_allowed(string path)
    {
        Assert.True(Policy().IsAllowed(path, out _));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Users\tester\.ssh\id_rsa")]
    [InlineData(@"C:\Users\tester\AppData\Roaming\Secrets\vault.db")]
    [InlineData(@"C:\Users\someone-else\Documents\a.txt")]
    [InlineData(@"C:\Users\tester\..\other\a.txt")]
    [InlineData(@"D:\Projects\app\Program.cs")]
    public void System_application_and_foreign_folders_are_blocked(string path)
    {
        Assert.False(Policy().IsAllowed(path, out var reason));
        Assert.NotEqual("", reason);
    }

    [Fact]
    public void A_folder_added_in_settings_becomes_allowed_without_opening_its_siblings()
    {
        _settings.AllowedFolders.Add(@"D:\Projects");
        var policy = Policy();
        Assert.True(policy.IsAllowed(@"D:\Projects\app\Program.cs", out _));
        Assert.True(policy.IsAllowed(@"D:\Projects", out _));
        Assert.False(policy.IsAllowed(@"D:\ProjectsOther\x.txt", out _));
    }

    [Fact]
    public void The_development_project_folder_is_allowed()
    {
        _settings.GetMode(JarvisMode.Development).ProjectPath = @"E:\code\jarvis";
        Assert.True(Policy().IsAllowed(@"E:\code\jarvis\src\a.cs", out _));
    }

    [Fact]
    public void Protected_folders_stay_blocked_even_when_configured_as_allowed()
    {
        _settings.AllowedFolders.Add(@"C:\Windows");
        Assert.False(Policy().IsAllowed(@"C:\Windows\win.ini", out _));
    }

    [Fact]
    public void Require_returns_the_full_path_or_throws_a_presentable_error()
    {
        var policy = Policy();
        Assert.Equal(@"C:\Users\tester\Downloads", policy.Require("downloads"));
        Assert.Throws<ToolArgumentException>(() => policy.Require(@"C:\Windows\win.ini"));
        Assert.Throws<ToolArgumentException>(() => policy.Require("relative.txt"));
    }
}

public class PermissionPolicyTests
{
    private static ToolDefinition Tool(ToolRisk risk) => new ToolDefinition("demo", "Demo.", ToolCategory.General, risk);

    [Fact]
    public void Consequential_actions_always_ask_even_when_set_to_allow()
    {
        var settings = new AppSettings();
        settings.ToolPermissions["demo"] = PermissionLevel.Allow;
        Assert.Equal(PermissionLevel.Ask, PermissionPolicy.Decide(Tool(ToolRisk.Consequential), ToolRisk.Consequential, settings));
    }

    [Fact]
    public void A_routine_tool_escalates_when_this_particular_call_is_consequential()
    {
        var settings = new AppSettings { TrustRoutineActions = true };
        Assert.Equal(PermissionLevel.Ask, PermissionPolicy.Decide(Tool(ToolRisk.Routine), ToolRisk.Consequential, settings));
    }

    [Theory]
    [InlineData(ToolRisk.Safe)]
    [InlineData(ToolRisk.Routine)]
    [InlineData(ToolRisk.Consequential)]
    public void A_blocked_tool_is_denied_at_every_risk_level(ToolRisk risk)
    {
        var settings = new AppSettings();
        settings.ToolPermissions["demo"] = PermissionLevel.Deny;
        Assert.Equal(PermissionLevel.Deny, PermissionPolicy.Decide(Tool(risk), risk, settings));
    }

    [Fact]
    public void Safe_actions_run_unless_the_user_asked_to_be_prompted()
    {
        var settings = new AppSettings();
        Assert.Equal(PermissionLevel.Allow, PermissionPolicy.Decide(Tool(ToolRisk.Safe), ToolRisk.Safe, settings));
        settings.ToolPermissions["demo"] = PermissionLevel.Ask;
        Assert.Equal(PermissionLevel.Ask, PermissionPolicy.Decide(Tool(ToolRisk.Safe), ToolRisk.Safe, settings));
    }

    [Fact]
    public void Routine_actions_follow_the_trust_switch_and_per_tool_overrides()
    {
        var settings = new AppSettings { TrustRoutineActions = true };
        Assert.Equal(PermissionLevel.Allow, PermissionPolicy.Decide(Tool(ToolRisk.Routine), ToolRisk.Routine, settings));

        settings.TrustRoutineActions = false;
        Assert.Equal(PermissionLevel.Ask, PermissionPolicy.Decide(Tool(ToolRisk.Routine), ToolRisk.Routine, settings));

        settings.ToolPermissions["demo"] = PermissionLevel.Allow;
        Assert.Equal(PermissionLevel.Allow, PermissionPolicy.Decide(Tool(ToolRisk.Routine), ToolRisk.Routine, settings));
    }

    [Fact]
    public void Settings_never_offer_allow_for_consequential_tools()
    {
        Assert.DoesNotContain(PermissionLevel.Allow, PermissionPolicy.SelectableLevels(ToolRisk.Consequential));
        Assert.Contains(PermissionLevel.Allow, PermissionPolicy.SelectableLevels(ToolRisk.Routine));
    }

    [Fact]
    public void Permission_overrides_survive_a_settings_round_trip_case_insensitively()
    {
        var settings = new AppSettings();
        settings.ToolPermissions["Demo"] = PermissionLevel.Deny;
        settings.AppAliases["VS Code"] = @"C:\Tools\Code.exe";

        var copy = settings.Clone();

        Assert.Equal(PermissionLevel.Deny, copy.ToolPermissions["demo"]);
        Assert.Equal(@"C:\Tools\Code.exe", copy.AppAliases["vs code"]);
        Assert.NotSame(settings.ToolPermissions, copy.ToolPermissions);
    }
}

public class SecretDetectorTests
{
    [Theory]
    [InlineData("my password is hunter2")]
    [InlineData("the api key = abc123")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuvw")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Credentials_are_recognised(string text)
    {
        Assert.True(SecretDetector.LooksSensitive(text, out var reason));
        Assert.NotEqual("", reason);
    }

    [Theory]
    [InlineData("I prefer dark mode")]
    [InlineData("My thesis deadline is 30 November")]
    [InlineData("The project lives in D:\\Projects\\jarvis")]
    public void Ordinary_facts_are_not_flagged(string text)
    {
        Assert.False(SecretDetector.LooksSensitive(text, out _));
    }

    [Fact]
    public void Key_value_secrets_are_masked()
    {
        Assert.Equal("token=[REDACTED]", SecretDetector.Redact("token=abcdef123456"));
    }

    [Fact]
    public void Bearer_tokens_are_masked()
    {
        Assert.Equal("Authorization: Bearer [REDACTED]", SecretDetector.Redact("Authorization: Bearer abc.def-ghi"));
    }

    [Fact]
    public void Api_keys_inside_longer_text_are_masked()
    {
        var redacted = SecretDetector.Redact("request failed for key AIzaSyA1234567890abcdefghijklmnopqrstuvw at 10:00");
        Assert.DoesNotContain("AIzaSyA", redacted);
        Assert.Contains("[REDACTED]", redacted);
        Assert.Contains("at 10:00", redacted);
    }

    [Fact]
    public void Ordinary_text_is_left_alone()
    {
        Assert.Equal("The meeting is at 5 pm", SecretDetector.Redact("The meeting is at 5 pm"));
        Assert.Equal("", SecretDetector.Redact(""));
    }
}
