using System.Runtime.InteropServices;
using LetsClip.Data.ViewModels;
using NAudio.CoreAudioApi;
using NLog;

namespace LetsClip.Services.Capture;

public static class AudioDevices
{
    // An Empty Id Means Whatever Windows Plays To Right Now
    public const string DefaultPlaybackId = "";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    public static IReadOnlyList<AudioDeviceInfo> Playback()
    {
        return List(DataFlow.Render);
    }

    public static IReadOnlyList<AudioDeviceInfo> Microphones()
    {
        return List(DataFlow.Capture);
    }

    public static string? DefaultName(bool playback)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.TryGetDefaultAudioEndpoint(
                playback ? DataFlow.Render : DataFlow.Capture,
                Role.Console,
                out var device
            )
                ? Describe(device).Name
                : null;
        }
        catch (COMException exception)
        {
            // No Audio Service Means No Devices, Never A Crash
            s_logger.Warn($"Failed To Read Default Audio Device {exception.GetType().Name}");
            return null;
        }
    }

    public static string? DefaultMicrophoneId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.TryGetDefaultAudioEndpoint(
                DataFlow.Capture,
                Role.Console,
                out var device
            )
                ? device.ID
                : null;
        }
        catch (COMException exception)
        {
            s_logger.Warn($"Failed To Read Default Microphone {exception.GetType().Name}");
            return null;
        }
    }

    private static AudioDeviceInfo Describe(MMDevice device)
    {
        // Headphones (G522) Reads As G522, Headphones Greyed Beside It
        var full = device.FriendlyName;
        var name = device.DeviceFriendlyName;
        var suffix = $" ({name})";
        return string.IsNullOrWhiteSpace(name) || !full.EndsWith(suffix, StringComparison.Ordinal)
            ? new AudioDeviceInfo(device.ID, full)
            : new AudioDeviceInfo(device.ID, name, full[..^suffix.Length]);
    }

    private static List<AudioDeviceInfo> List(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return
            [
                .. enumerator
                    .EnumerateAudioEndPoints(flow, DeviceState.Active)
                    .Select(Describe)
                    .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase),
            ];
        }
        catch (COMException exception)
        {
            s_logger.Warn($"Failed To List Audio Devices {exception.GetType().Name}");
            return [];
        }
    }
}
