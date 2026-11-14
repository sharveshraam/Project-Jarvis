using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using Jarvis.Core.Abstractions;

namespace Jarvis.Infrastructure.Platform;

/// <summary>
/// Runs one executable with an explicit argument list (never through cmd/PowerShell), so arguments are
/// passed verbatim and cannot be interpreted as shell syntax.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxOutputChars = 20_000;

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? "" : workingDirectory
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new ProcessResult(-1, "", $"'{executable}' could not be started.", false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
        {
            return new ProcessResult(-1, "", $"'{executable}' could not be started: {ex.Message}", false);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, "", $"'{executable}' did not finish within {timeout.TotalSeconds:0} seconds and was stopped.", true);
        }

        return new ProcessResult(process.ExitCode, Clip(await stdout.ConfigureAwait(false)), Clip(await stderr.ConfigureAwait(false)), false);
    }

    private static string Clip(string text)
        => text.Length <= MaxOutputChars ? text : text.Substring(0, MaxOutputChars) + "\n… (output truncated)";
}

/// <summary>Event-driven network availability; nothing is polled.</summary>
public sealed class Connectivity : IConnectivity, IDisposable
{
    public Connectivity()
    {
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
    }

    public bool IsNetworkAvailable
    {
        get
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch (NetworkInformationException) { return true; }
        }
    }

    public event Action? Changed;

    private void OnChanged(object? sender, NetworkAvailabilityEventArgs e) => Changed?.Invoke();

    public void Dispose() => NetworkChange.NetworkAvailabilityChanged -= OnChanged;
}
