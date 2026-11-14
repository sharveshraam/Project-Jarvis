using System.Runtime.InteropServices;
using System.Text.Json;

namespace Jarvis.Windows;

/// <summary>System volume via Core Audio (IAudioEndpointVolume COM).
/// Kept in its own file so the COM interop is isolated and testable.</summary>
public static class AudioControl
{
    public static string SetVolume(JsonElement args)
    {
        try
        {
            var endpoint = CreateEndpoint();
            if (endpoint is null) return "Volume control is unavailable on this machine.";
            if (args.TryGetProperty("mute", out var mute))
            {
                var m = mute.GetBoolean();
                endpoint.SetMute(m, Guid.Empty);
                return m ? "Muted." : "Unmuted.";
            }
            if (args.TryGetProperty("level", out var level))
            {
                var v = Math.Clamp(level.GetInt32(), 0, 100) / 100f;
                endpoint.SetMasterVolumeLevelScalar(v, Guid.Empty);
                return $"Volume set to {level.GetInt32()}%.";
            }
            return "Specify a volume level (0-100) or mute true/false.";
        }
        catch (Exception ex)
        {
            return $"Volume control failed: {ex.Message}";
        }
    }

    private static IAudioEndpointVolume? CreateEndpoint()
    {
        // MMDeviceEnumerator COM activation — standard Core Audio pattern.
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        if (enumeratorType is null) return null;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        enumerator.GetDefaultAudioEndpoint(0, 0, out var device);
        device.Activate(typeof(IAudioEndpointVolume).GUID, 0, IntPtr.Zero, out var volume);
        return (IAudioEndpointVolume)volume;
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(bool mute, Guid context);
        [PreserveSig] int GetMute(out bool mute);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int NotUsed1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7D479-1BE7-4F15-BF0B-6F1B0A5C9F5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(Guid interfaceId, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }
}
