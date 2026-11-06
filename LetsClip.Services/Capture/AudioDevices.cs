using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LetsClip.Data.ViewModels;
using NAudio.CoreAudioApi;
using NLog;

namespace LetsClip.Services.Capture;

public static partial class AudioDevices
{
    // An Empty Id Follows The Windows Default Device
    public const string AutomaticId = "";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    static AudioDevices()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static IReadOnlyList<AudioDeviceInfo> Playback()
    {
        return List(DataFlow.Render);
    }

    public static IReadOnlyList<AudioDeviceInfo> Microphones()
    {
        return List(DataFlow.Capture);
    }

    public static MMDevice? Find(MMDeviceEnumerator enumerator, string id)
    {
        // The Picked Device While Active, Else The Default
        try
        {
            return id.Length > 0 && enumerator.GetDevice(id) is { State: DeviceState.Active } device
                ? device
                : null;
        }
        catch (COMException)
        {
            // A Device Windows No Longer Knows
            return null;
        }
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
            List<AudioDeviceInfo> devices =
            [
                .. enumerator
                    .EnumerateAudioEndPoints(flow, DeviceState.Active)
                    .Select(device =>
                    {
                        var (name, adapter) = NamesOf(
                            device.FriendlyName,
                            device.DeviceFriendlyName
                        );
                        return new AudioDeviceInfo(
                            device.ID,
                            name,
                            adapter,
                            device.ID == defaultId
                        );
                    })
                    .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(device => device.Adapter, StringComparer.CurrentCultureIgnoreCase),
            ];
            s_logger.Debug(
                $"Found {flow} Devices: {string.Join("; ", devices.Select(device => $"{device.Name} ({device.Adapter}){(device.IsDefault ? " Default" : "")}"))}"
            );
            return devices;
        }
        catch (COMException exception)
        {
            // No Audio Service Means No Devices, Never A Crash
            s_logger.Warn($"Failed To List Audio Devices {exception.GetType().Name}");
            return [];
        }
    }

    public static (string Name, string? Adapter) NamesOf(string friendlyName, string? adapterName)
    {
        // Windows' Own Split, Microphone Array With Its Adapter Beside It
        var suffix = $" ({adapterName})";
        var endpoint =
            adapterName is { Length: > 0 }
            && friendlyName.EndsWith(suffix, StringComparison.Ordinal)
                ? friendlyName[..^suffix.Length]
                : friendlyName;
        var name = Clean(endpoint) ?? friendlyName;
        var adapter = Clean(adapterName);
        return (name, adapter == name ? null : adapter);
    }

    private static string? Clean(string? text)
    {
        // Trademark Marks Go, Their Spaces With Them
        var cleaned = Spaces().Replace(Trademarks().Replace(text ?? "", ""), " ").Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }

    [GeneratedRegex(@"\((R|TM)\)|®|™", RegexOptions.IgnoreCase)]
    private static partial Regex Trademarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static bool SelfTestPasses()
    {
        return NamesOf(
                "Microphone Array (Intel® Smart Sound Technologie voor digitale microfoons)",
                "Intel® Smart Sound Technologie voor digitale microfoons"
            ) == ("Microphone Array", "Intel Smart Sound Technologie voor digitale microfoons")
            && NamesOf("Headphones (G522)", "G522") == ("Headphones", "G522")
            && NamesOf("Luidsprekers (Realtek(R) Audio)", "Realtek(R) Audio")
                == ("Luidsprekers", "Realtek Audio")
            && NamesOf("Speakers", null) == ("Speakers", null)
            && NamesOf("Odd (Name)", "Other") == ("Odd (Name)", "Other");
    }
}
