using System.ComponentModel;
using System.Diagnostics;
using Jarvis.Core.Abstractions;

namespace Jarvis.Windows;

/// <summary>Hands files, folders, URLs and settings pages to Windows. Every process start uses a fixed executable and an argument list.</summary>
public sealed class ShellService : IShellService
{
    private static readonly Dictionary<string, string> SettingsPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["display"] = "ms-settings:display", ["sound"] = "ms-settings:sound", ["bluetooth"] = "ms-settings:bluetooth",
        ["wifi"] = "ms-settings:network-wifi", ["network"] = "ms-settings:network-status", ["battery"] = "ms-settings:batterysaver",
        ["storage"] = "ms-settings:storagesense", ["apps"] = "ms-settings:appsfeatures", ["startup"] = "ms-settings:startupapps",
        ["updates"] = "ms-settings:windowsupdate", ["privacy"] = "ms-settings:privacy", ["notifications"] = "ms-settings:notifications",
        ["personalization"] = "ms-settings:personalization", ["about"] = "ms-settings:about", ["focus"] = "ms-settings:quiethours",
        ["microphone"] = "ms-settings:privacy-microphone", ["default_apps"] = "ms-settings:defaultapps"
    };

    private static ActionOutcome Start(ProcessStartInfo info, string success, bool verified)
    {
        try
        {
            using var process = Process.Start(info);
            return new ActionOutcome(true, success, verified);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ActionOutcome(false, "Windows could not open it: " + ex.Message);
        }
    }

    public ActionOutcome OpenPath(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            info.ArgumentList.Add(path);
            return Start(info, $"Opened {path} in File Explorer.", true);
        }
        if (!File.Exists(path)) return new ActionOutcome(false, $"'{path}' does not exist.");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".exe" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".msi" or ".scr" or ".com" or ".lnk" or ".reg" or ".hta" or ".wsf")
            return new ActionOutcome(false, "That file is a program or script. JARVIS opens documents and folders, and starts applications only by name.");

        return Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }, $"Opened {Path.GetFileName(path)}.", false);
    }

    public ActionOutcome OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ActionOutcome(false, "Only http and https addresses can be opened.");
        return Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true }, $"Opened {uri.Host} in the browser.", false);
    }

    public ActionOutcome OpenSettingsPage(string page)
    {
        if (!SettingsPages.TryGetValue(page.Trim(), out var target)) return new ActionOutcome(false, $"There is no settings page called '{page}'.");
        return Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }, $"Opened Windows Settings › {page}.", false);
    }

    public ActionOutcome OpenTerminal(string? workingDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : workingDirectory;

        var terminal = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = false };
        terminal.ArgumentList.Add("-d");
        terminal.ArgumentList.Add(directory);
        var outcome = Start(terminal, $"Opened Windows Terminal in {directory}.", true);
        if (outcome.Success) return outcome;

        // Windows Terminal is not installed: fall back to a plain PowerShell window in the same folder.
        var fallback = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = true, WorkingDirectory = directory };
        return Start(fallback, $"Windows Terminal is not installed, so I opened PowerShell in {directory}.", true);
    }

    public ActionOutcome OpenInIde(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return new ActionOutcome(false, $"'{path}' does not exist.");
        var code = IdeLocator.FindVsCode();
        if (code is null) return new ActionOutcome(false, "Visual Studio Code was not found in its standard install location.");
        var info = new ProcessStartInfo { FileName = code, UseShellExecute = false };
        info.ArgumentList.Add(path);
        return Start(info, $"Opened {path} in VS Code.", true);
    }

    public ActionOutcome LockWorkstation()
        => Native.LockWorkStation()
            ? new ActionOutcome(true, "The computer is locked.", true)
            : new ActionOutcome(false, "Windows refused to lock the session.");
}
