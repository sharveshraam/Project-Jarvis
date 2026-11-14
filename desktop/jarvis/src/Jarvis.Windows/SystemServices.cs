using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Jarvis.Core.Abstractions;

namespace Jarvis.Windows;

/// <summary>On-demand system sampling. Nothing here runs unless a request asks for it.</summary>
public sealed class SystemInfoService : ISystemInfo
{
    public async Task<SystemSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        Native.GetSystemTimes(out var idle1, out var kernel1, out var user1);
        await Task.Delay(450, ct).ConfigureAwait(false);
        Native.GetSystemTimes(out var idle2, out var kernel2, out var user2);
        var total = (kernel2 - kernel1) + (user2 - user1);
        var cpu = total > 0 ? Math.Clamp(100.0 * (total - (idle2 - idle1)) / total, 0, 100) : 0;

        long ramTotal = 0, ramUsed = 0;
        var memory = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref memory))
        {
            ramTotal = (long)memory.ullTotalPhys;
            ramUsed = (long)(memory.ullTotalPhys - memory.ullAvailPhys);
        }

        var drives = new List<DriveStatus>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    drives.Add(new DriveStatus(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new SystemSnapshot(cpu, ramTotal, ramUsed, drives, ReadBattery(), await Task.Run(ReadGpus, ct).ConfigureAwait(false),
            RuntimeInformation.OSDescription, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static BatteryStatus? ReadBattery()
    {
        try
        {
            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            if ((power.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) != 0) return null;
            if (power.BatteryLifePercent > 1.0f) return null;
            var percent = (int)Math.Round(power.BatteryLifePercent * 100);
            var charging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            int? minutes = power.BatteryLifeRemaining > 0 ? power.BatteryLifeRemaining / 60 : null;
            return new BatteryStatus(percent, charging, minutes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadGpus()
    {
        var names = new List<string>();
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            using (var results = searcher.Get())
            {
                foreach (var item in results)
                {
                    var name = item["Name"] as string;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                    item.Dispose();
                }
            }

            // Overall 3D-engine utilisation, when the driver publishes GPU performance counters.
            double load = 0;
            var any = false;
            using (var searcher = new ManagementObjectSearcher("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            using (var results = searcher.Get())
            {
                foreach (var item in results)
                {
                    var engine = item["Name"] as string ?? "";
                    if (engine.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        load += Convert.ToDouble(item["UtilizationPercentage"] ?? 0, System.Globalization.CultureInfo.InvariantCulture);
                        any = true;
                    }
                    item.Dispose();
                }
            }
            if (any && names.Count > 0) names[0] = $"{names[0]} ({Math.Clamp(load, 0, 100):0}% in use)";
        }
        catch (Exception)
        {
            // WMI unavailable or counters missing: report what was found.
        }
        return names;
    }

    public IReadOnlyList<ProcessUsage> TopProcesses(int count)
    {
        var list = new List<ProcessUsage>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                double cpuSeconds = 0;
                try { cpuSeconds = process.TotalProcessorTime.TotalSeconds; }
                catch (Exception) { /* protected process */ }
                list.Add(new ProcessUsage(process.Id, process.ProcessName, process.WorkingSet64, cpuSeconds));
            }
            catch (Exception)
            {
                // The process exited while being inspected.
            }
            finally
            {
                process.Dispose();
            }
        }
        return list.OrderByDescending(p => p.MemoryBytes).Take(Math.Clamp(count, 1, 50)).ToList();
    }
}

public sealed class AudioController : IAudioController
{
    private static CoreAudio.IAudioEndpointVolume Endpoint()
    {
        var enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumerator();
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(CoreAudio.ERender, CoreAudio.EMultimedia, out var device);
            if (hr != 0 || device is null) throw new InvalidOperationException("No audio output device is available.");
            try
            {
                var iid = CoreAudio.AudioEndpointVolumeId;
                hr = device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var instance);
                if (hr != 0 || instance is null) throw new InvalidOperationException("The audio device does not expose a volume control.");
                return (CoreAudio.IAudioEndpointVolume)instance;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public int GetVolume()
    {
        var endpoint = Endpoint();
        try
        {
            Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var level));
            return (int)Math.Round(level * 100);
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public void SetVolume(int percent)
    {
        var endpoint = Endpoint();
        try
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context));
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public bool IsMuted()
    {
        var endpoint = Endpoint();
        try
        {
            Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
            return muted;
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }

    public void SetMuted(bool muted)
    {
        var endpoint = Endpoint();
        try
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(endpoint.SetMute(muted, ref context));
        }
        finally
        {
            Marshal.ReleaseComObject(endpoint);
        }
    }
}

/// <summary>Brightness of the built-in panel through WMI. External monitors generally do not expose this interface.</summary>
public sealed class DisplayController : IDisplayController
{
    private const string Unsupported = "This display does not support software brightness control (common for external monitors). Use the monitor's own buttons.";

    public int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                var value = Convert.ToInt32(item["CurrentBrightness"], System.Globalization.CultureInfo.InvariantCulture);
                item.Dispose();
                return value;
            }
        }
        catch (Exception)
        {
            // Not supported on this hardware.
        }
        return null;
    }

    public bool TrySetBrightness(int percent, out string error)
    {
        error = "";
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            var applied = false;
            foreach (var item in results)
            {
                using var monitor = (ManagementObject)item;
                monitor.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
                applied = true;
            }
            if (!applied) error = Unsupported;
            return applied;
        }
        catch (Exception)
        {
            error = Unsupported;
            return false;
        }
    }
}
