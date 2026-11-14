using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Windows;

/// <summary>Windows operations exposed as JARVIS tools. Every operation is
/// on-demand (no background polling, no continuous screen/clipboard access).
/// Destructive operations are never exposed as tools at all.</summary>
public sealed class WindowsTools(ILogger<WindowsTools>? logger = null)
{
    private readonly ILogger<WindowsTools> _log = logger ?? NullLogger<WindowsTools>.Instance;

    public ToolDefinition OpenAppTool => new("open_app", "Launch an application by name or path.",
        RiskTier.Trusted, """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
        async (args, ct) => await Task.Run(() => OpenApp(args.GetProperty("name").GetString() ?? ""), ct));

    public ToolDefinition CloseAppTool => new("close_app", "Gracefully close an application by name (requests close, never force-kills).",
        RiskTier.ApprovalRequired, """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
        async (args, ct) => await Task.Run(() => CloseApp(args.GetProperty("name").GetString() ?? ""), ct));

    public ToolDefinition SwitchWindowTool => new("switch_window", "Bring a window of the named application to the foreground.",
        RiskTier.Trusted, """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
        async (args, ct) => await Task.Run(() => SwitchWindow(args.GetProperty("name").GetString() ?? ""), ct));

    public ToolDefinition OpenFolderTool => new("open_folder", "Open a folder in Explorer.",
        RiskTier.Trusted, """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""",
        async (args, ct) => await Task.Run(() => OpenPath(args.GetProperty("path").GetString() ?? ""), ct));

    public ToolDefinition OpenFileTool => new("open_file", "Open a file with its default application.",
        RiskTier.Trusted, """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""",
        async (args, ct) => await Task.Run(() => OpenPath(args.GetProperty("path").GetString() ?? ""), ct));

    public ToolDefinition SystemInfoTool => new("get_system_info", "Report CPU, memory, battery, storage and running-process summary (on demand).",
        RiskTier.Trusted, """{"type":"object","properties":{}}""",
        async (_, ct) => await Task.Run(GetSystemInfo, ct));

    public ToolDefinition SetVolumeTool => new("set_volume", "Set system volume 0-100 or mute/unmute.",
        RiskTier.ApprovalRequired, """{"type":"object","properties":{"level":{"type":"integer"},"mute":{"type":"boolean"}}}""",
        async (args, ct) => await Task.Run(() => SetVolume(args), ct));

    public ToolDefinition ReadClipboardTool => new("read_clipboard_summary", "Read the clipboard ONCE (only when explicitly requested) and return its text for analysis.",
        RiskTier.ApprovalRequired, """{"type":"object","properties":{}}""",
        async (_, ct) => await Task.Run(ReadClipboardOnce, ct));

    public ToolDefinition CaptureScreenTool => new("capture_screen", "Capture the screen ONCE (only when explicitly requested) and return a PNG path for Gemini analysis.",
        RiskTier.ApprovalRequired, """{"type":"object","properties":{}}""",
        async (_, ct) => await Task.Run(CaptureScreenOnce, ct));

    public IEnumerable<ToolDefinition> All => [OpenAppTool, CloseAppTool, SwitchWindowTool, OpenFolderTool, OpenFileTool, SystemInfoTool, SetVolumeTool, ReadClipboardTool, CaptureScreenTool];

    private string OpenApp(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "No application name given.";
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = "notepad.exe", ["calculator"] = "calc.exe", ["code"] = "code",
            ["vscode"] = "code", ["terminal"] = "wt.exe", ["cmd"] = "cmd.exe",
            ["explorer"] = "explorer.exe", ["settings"] = "ms-settings:", ["paint"] = "mspaint.exe",
            ["browser"] = "msedge.exe", ["chrome"] = "chrome.exe", ["firefox"] = "firefox.exe",
            ["spotify"] = "spotify.exe", ["discord"] = "Discord.exe"
        };
        var target = known.TryGetValue(name.Trim(), out var mapped) ? mapped : name.Trim();
        try
        {
            if (target.StartsWith("ms-settings:")) { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return $"Opened {name}."; }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return $"Launched {name}.";
        }
        catch (Exception ex) { _log.LogWarning(ex, "OpenApp failed for {Name}", name); return $"Could not launch {name}: {ex.Message}"; }
    }

    private string CloseApp(string name)
    {
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
        if (procs.Length == 0) return $"{name} is not running.";
        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { /* already exiting */ }
        }
        return $"Requested a graceful close of {name} ({procs.Length} process(es)). Unsaved work is respected — force-kill is never used.";
    }

    private string SwitchWindow(string name)
    {
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
        if (procs.Length == 0) return $"{name} is not running.";
        foreach (var p in procs)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    return $"Brought {name} to the foreground.";
                }
            }
            catch { /* try next */ }
        }
        return $"{name} is running but has no visible window.";
    }

    private string OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.Exists(path)) return $"Path not found: {path}";
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return $"Opened {path}.";
    }

    public string GetSystemInfo()
    {
        var lines = new List<string>();
        try
        {
            var os = Environment.OSVersion;
            lines.Add($"OS: {os.Platform} {os.Version}");
            lines.Add($"Machine: {Environment.MachineName}");
            lines.Add($"CPU cores: {Environment.ProcessorCount}");
            var gc = GC.GetGCMemoryInfo();
            lines.Add($"Managed heap: {gc.HeapSizeBytes / 1024 / 1024} MB");
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);
            foreach (var d in drives)
                lines.Add($"Drive {d.Name}: {d.TotalFreeSpace / 1024 / 1024 / 1024} GB free of {d.TotalSize / 1024 / 1024 / 1024} GB");
            var top = Process.GetProcesses().OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } }).Take(5);
            lines.Add("Top processes by memory:");
            foreach (var p in top) { try { lines.Add($"- {p.ProcessName}: {p.WorkingSet64 / 1024 / 1024} MB"); } catch { } }
            lines.Add($"Battery: {SystemParametersInfo()}");
        }
        catch (Exception ex) { lines.Add($"System info partially unavailable: {ex.Message}"); }
        return string.Join("\n", lines);
    }

    private static string SystemParametersInfo()
    {
        // Battery via WMI-free P/Invoke is unreliable across hardware; report honestly.
        return "Battery level is not reliably available via this API on this machine (reported as unavailable rather than guessed).";
    }

    private string SetVolume(JsonElement args)
    {
        // Volume control requires Core Audio COM; implemented via the audio endpoint
        // in AudioControl.cs. This stub delegates there.
        return AudioControl.SetVolume(args);
    }

    private string ReadClipboardOnce()
    {
        string? text = null;
        var t = new Thread(() =>
        {
            try { if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText(); }
            catch (Exception ex) { text = $"Clipboard unavailable: {ex.Message}"; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join(TimeSpan.FromSeconds(5));
        return text is null ? "Clipboard is empty or unavailable." : $"Clipboard contents (read once, on your request):\n{text}";
    }

    private string CaptureScreenOnce()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JARVIS", "captures");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"screen-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
