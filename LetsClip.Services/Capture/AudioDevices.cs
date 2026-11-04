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

    private static List<AudioDeviceInfo> List(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = enumerator.TryGetDefaultAudioEndpoint(
                flow,
                Role.Console,
                out var current
            )
                ? current.ID
                : null;
            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();

            // Headphones (G522) Reads As G522 Unless Names Then Clash
            var shortNames = devices.Select(ShortName).ToList();
            return
            [
                .. devices
                    .Select(
                        (device, index) =>
                            new AudioDeviceInfo(
                                device.ID,
                                shortNames.Count(name => name == shortNames[index]) > 1
                                    ? device.FriendlyName
                                    : shortNames[index],
                                device.ID == defaultId
                            )
                    )
                    .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase),
            ];
        }
        catch (COMException exception)
        {
            // No Audio Service Means No Devices, Never A Crash
            s_logger.Warn($"Failed To List Audio Devices {exception.GetType().Name}");
            return [];
        }
    }

    private static string ShortName(MMDevice device)
    {
        var full = device.FriendlyName;
        var adapter = device.DeviceFriendlyName;
        return
            !string.IsNullOrWhiteSpace(adapter)
            && full.EndsWith($" ({adapter})", StringComparison.Ordinal)
            ? adapter
            : full;
    }
}
