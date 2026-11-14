using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Files;
using Jarvis.Core.Tools;

namespace Jarvis.Agent.Tools;

/// <summary>System awareness and everyday settings. Everything is sampled once, on request; nothing is monitored.</summary>
public sealed class SystemTools : ToolModule
{
    public static readonly string[] SettingsPages =
    {
        "display", "sound", "bluetooth", "wifi", "network", "battery", "storage", "apps", "startup", "updates",
        "privacy", "notifications", "personalization", "about", "focus", "microphone", "default_apps"
    };

    private readonly ISystemInfo _system;
    private readonly IAudioController _audio;
    private readonly IDisplayController _display;
    private readonly IShellService _shell;

    public SystemTools(ISystemInfo system, IAudioController audio, IDisplayController display, IShellService shell)
    {
        _system = system;
        _audio = audio;
        _display = display;
        _shell = shell;
    }

    public static string Describe(SystemSnapshot s, int? volume, bool? muted, int? brightness)
    {
        var sb = new StringBuilder();
        sb.Append("CPU ").Append(s.CpuPercent.ToString("0", CultureInfo.InvariantCulture)).Append("% in use");
        if (s.RamTotalBytes > 0)
        {
            var percent = 100.0 * s.RamUsedBytes / s.RamTotalBytes;
            sb.Append("\nMemory ").Append(FileService.FormatSize(s.RamUsedBytes)).Append(" of ").Append(FileService.FormatSize(s.RamTotalBytes))
                .Append(" (").Append(percent.ToString("0", CultureInfo.InvariantCulture)).Append("%)");
        }
        foreach (var d in s.Drives)
        {
            var used = d.TotalBytes > 0 ? 100.0 * (d.TotalBytes - d.FreeBytes) / d.TotalBytes : 0;
            sb.Append("\nDrive ").Append(d.Name).Append(' ').Append(FileService.FormatSize(d.FreeBytes)).Append(" free of ")
                .Append(FileService.FormatSize(d.TotalBytes)).Append(" (").Append(used.ToString("0", CultureInfo.InvariantCulture)).Append("% used)");
        }
        if (s.Battery is not null)
        {
            sb.Append("\nBattery ").Append(s.Battery.Percent).Append('%').Append(s.Battery.Charging ? ", charging" : ", on battery");
            if (s.Battery.MinutesRemaining.HasValue && !s.Battery.Charging)
                sb.Append(", about ").Append(s.Battery.MinutesRemaining.Value / 60).Append(" h ").Append(s.Battery.MinutesRemaining.Value % 60).Append(" min left");
        }
        else
        {
            sb.Append("\nBattery: none detected (desktop power)");
        }
        if (s.Gpus.Count > 0) sb.Append("\nGPU ").Append(string.Join(", ", s.Gpus));
        if (volume.HasValue) sb.Append("\nVolume ").Append(volume.Value).Append('%').Append(muted == true ? " (muted)" : "");
        if (brightness.HasValue) sb.Append("\nBrightness ").Append(brightness.Value).Append('%');
        sb.Append("\nUptime ").Append((int)s.Uptime.TotalHours).Append(" h ").Append(s.Uptime.Minutes).Append(" min — ").Append(s.OsDescription);
        return sb.ToString();
    }

    private int? SafeVolume()
    {
        try { return _audio.GetVolume(); }
        catch (Exception) { return null; }
    }

    private bool? SafeMuted()
    {
        try { return _audio.IsMuted(); }
        catch (Exception) { return null; }
    }

    public override IEnumerable<ITool> Build()
    {
        yield return Tool(
            Def("get_system_status", "Check the computer's current CPU, memory, storage, battery, GPU, volume and brightness. Sampled once, now.", ToolCategory.System, ToolRisk.Safe).Retryable(),
            async (args, context, ct) =>
            {
                var snapshot = await _system.GetSnapshotAsync(ct).ConfigureAwait(false);
                var volume = SafeVolume();
                var muted = SafeMuted();
                var brightness = _display.GetBrightness();
                var data = new JsonObject
                {
                    ["cpu_percent"] = Math.Round(snapshot.CpuPercent, 1),
                    ["ram_used_bytes"] = snapshot.RamUsedBytes,
                    ["ram_total_bytes"] = snapshot.RamTotalBytes,
                    ["battery_percent"] = snapshot.Battery?.Percent,
                    ["charging"] = snapshot.Battery?.Charging,
                    ["volume_percent"] = volume,
                    ["muted"] = muted,
                    ["brightness_percent"] = brightness
                };
                return ToolResult.Ok(Describe(snapshot, volume, muted, brightness), true, data);
            });

        yield return Sync(
            Def("get_top_processes", "List the processes using the most memory right now.", ToolCategory.System, ToolRisk.Safe)
                .Int("count", "How many to list (default 8).", false, 1, 25).Retryable(),
            (args, context) =>
            {
                var top = _system.TopProcesses(args.Int("count", 8));
                var sb = new StringBuilder("Heaviest processes by memory:");
                foreach (var p in top)
                    sb.Append("\n• ").Append(p.Name).Append(" — ").Append(FileService.FormatSize(p.MemoryBytes))
                        .Append(", ").Append(p.CpuSeconds.ToString("0", CultureInfo.InvariantCulture)).Append(" s CPU time");
                return ToolResult.Ok(sb.ToString());
            });

        yield return Sync(
            Def("set_volume", "Set the system output volume.", ToolCategory.System, ToolRisk.Routine)
                .Int("percent", "Volume from 0 to 100.", true, 0, 100).Retryable(),
            (args, context) =>
            {
                var target = Math.Clamp(args.Int("percent", 50), 0, 100);
                _audio.SetVolume(target);
                var actual = _audio.GetVolume();
                return Math.Abs(actual - target) <= 2
                    ? ToolResult.Ok($"Volume set to {actual}%.")
                    : ToolResult.Fail($"The volume was requested at {target}% but reads {actual}%.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Set volume to {args.Str("percent")}%"),
            args => Math.Abs(_audio.GetVolume() - args.Int("percent", -100)) <= 2);

        yield return Sync(
            Def("set_mute", "Mute or unmute the system output.", ToolCategory.System, ToolRisk.Routine)
                .Bool("muted", "true to mute, false to unmute.", true).Retryable(),
            (args, context) =>
            {
                var target = args.Bool("muted");
                _audio.SetMuted(target);
                return _audio.IsMuted() == target
                    ? ToolResult.Ok(target ? "Sound muted." : "Sound unmuted.")
                    : ToolResult.Fail("The mute state did not change.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, args.Bool("muted") ? "Mute sound" : "Unmute sound"),
            args => _audio.IsMuted() == args.Bool("muted"));

        yield return Sync(
            Def("set_brightness", "Set the built-in display's brightness (laptops and displays that support software control).", ToolCategory.System, ToolRisk.Routine)
                .Int("percent", "Brightness from 0 to 100.", true, 0, 100).Retryable(),
            (args, context) =>
            {
                var target = Math.Clamp(args.Int("percent", 50), 0, 100);
                if (!_display.TrySetBrightness(target, out var error)) return ToolResult.Fail(error);
                var actual = _display.GetBrightness();
                if (!actual.HasValue) return ToolResult.Ok($"Brightness change to {target}% was requested, but this display does not report its level.", false);
                return Math.Abs(actual.Value - target) <= 3
                    ? ToolResult.Ok($"Brightness set to {actual.Value}%.")
                    : ToolResult.Fail($"Brightness was requested at {target}% but reads {actual.Value}%.");
            },
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, $"Set brightness to {args.Str("percent")}%"));

        yield return Sync(
            Def("open_settings_page", "Open a page of the Windows Settings app.", ToolCategory.System, ToolRisk.Routine)
                .Enum("page", "Which settings page.", true, SettingsPages).Retryable(),
            (args, context) => From(_shell.OpenSettingsPage(args.Required("page"))),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Open Windows Settings › " + args.Str("page")));

        yield return Sync(
            Def("lock_computer", "Lock the Windows session.", ToolCategory.System, ToolRisk.Routine),
            (args, context) => From(_shell.LockWorkstation()),
            (args, context) => ToolAssessment.Of(ToolRisk.Routine, "Lock the computer"));
    }
}
