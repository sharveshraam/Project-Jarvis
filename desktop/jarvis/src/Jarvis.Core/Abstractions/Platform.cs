using Jarvis.Core.AI;
using Jarvis.Core.Models;

namespace Jarvis.Core.Abstractions;

public sealed record ActionOutcome(bool Success, string Message, bool Verified = false);
public sealed record RunningApp(int ProcessId, string ProcessName, string WindowTitle);

public interface IAppController
{
    /// <summary>Launches a known/configured/installed application by friendly name, or an explicit .exe/.lnk path.</summary>
    ActionOutcome Launch(string nameOrPath);
    /// <summary>Asks the app to close (WM_CLOSE). Never force-kills, so unsaved-work prompts are respected.</summary>
    Task<ActionOutcome> CloseAsync(string name, CancellationToken ct);
    ActionOutcome SwitchTo(string name);
    IReadOnlyList<RunningApp> ListRunning();
    bool IsRunning(string name);
}

public interface IShellService
{
    ActionOutcome OpenPath(string path);
    ActionOutcome OpenUrl(string url);
    ActionOutcome OpenSettingsPage(string page);
    ActionOutcome OpenTerminal(string? workingDirectory);
    ActionOutcome OpenInIde(string path);
    ActionOutcome LockWorkstation();
}

public sealed record DriveStatus(string Name, long TotalBytes, long FreeBytes);
public sealed record BatteryStatus(int Percent, bool Charging, int? MinutesRemaining);
public sealed record ProcessUsage(int ProcessId, string Name, long MemoryBytes, double CpuSeconds);
public sealed record SystemSnapshot(
    double CpuPercent, long RamTotalBytes, long RamUsedBytes, IReadOnlyList<DriveStatus> Drives,
    BatteryStatus? Battery, IReadOnlyList<string> Gpus, string OsDescription, TimeSpan Uptime);

public interface ISystemInfo
{
    /// <summary>Samples system state once, on demand. Nothing is monitored in the background.</summary>
    Task<SystemSnapshot> GetSnapshotAsync(CancellationToken ct);
    IReadOnlyList<ProcessUsage> TopProcesses(int count);
}

public interface IAudioController
{
    int GetVolume();
    void SetVolume(int percent);
    bool IsMuted();
    void SetMuted(bool muted);
}

public interface IDisplayController
{
    int? GetBrightness();
    bool TrySetBrightness(int percent, out string error);
}

public sealed record CapturedImage(byte[] Data, string MimeType, int Width, int Height)
{
    public AiImage ToAiImage() => new(Data, MimeType);
}

public interface IScreenCapture
{
    /// <summary>Captures the screen once. Only ever called for an explicit user request.</summary>
    Task<CapturedImage> CaptureAsync(CancellationToken ct);
}

public interface IClipboardService
{
    /// <summary>Reads clipboard text once. Only ever called for an explicit user request.</summary>
    string? GetText();
}

public interface ISpeechToText
{
    bool IsAvailable(out string reason);
    bool IsListening { get; }
    event Action<string>? PartialResult;
    /// <summary>Opens the microphone. Called only from an explicit push-to-talk action.</summary>
    void Start();
    /// <summary>Closes the microphone and returns everything recognised since <see cref="Start"/>.</summary>
    Task<string> StopAsync();
}

public interface ITextToSpeech
{
    bool IsSpeaking { get; }
    Task SpeakAsync(string text, CancellationToken ct);
    void Stop();
}

public interface INotifier
{
    void Notify(string title, string message, NotificationKind kind = NotificationKind.Info);
}

public sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);

public interface IProcessRunner
{
    /// <summary>Runs an executable with an explicit argument list. No shell is involved, so nothing is interpreted.</summary>
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory,
        TimeSpan timeout, CancellationToken ct);
}

public interface IStartupManager
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

public interface IConnectivity
{
    bool IsNetworkAvailable { get; }
    event Action? Changed;
}
