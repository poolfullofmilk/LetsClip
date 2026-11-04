using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;
using HotkeyGesture = LetsClip.Data.Helpers.Hotkey;

namespace LetsClip.Services.Storage;

public sealed record UserSettings(
    string? MonitorDevicePath = null,
    string? MonitorName = null,
    int ReplaySeconds = 30,
    int FrameRate = 60,
    bool FullResolution = false,
    bool GamesOnly = false,
    string[]? RemovedGames = null,
    AddedGame[]? AddedGames = null,
    bool StartWithWindows = true,
    bool SaveSound = true,
    bool SystemAudio = true,
    string SystemAudioDeviceId = "",
    bool Microphone = false,
    string? MicrophoneDeviceId = null,
    string Hotkey = "Ctrl+Shift+F8",
    string? ClipsFolder = null,
    double ExportMegabytes = 10,
    bool Export720p = false,
    bool ExportFullFrameRate = false,
    bool HasSeenTrayHint = false,
    bool HasSeenCaptureNotice = false
)
{
    public static readonly int[] ReplayChoices = [30, 60, 120];
    public static readonly int[] FrameRateChoices = [30, 60, 120, 144, 165, 240];

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly string s_filePath = Path.Combine(
        AppInfo.DataDirectory,
        "settings.json"
    );
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonIgnore]
    public string ClipsDirectory =>
        string.IsNullOrWhiteSpace(ClipsFolder) ? AppInfo.DefaultClipsDirectory : ClipsFolder;

    [JsonIgnore]
    public string ExportsDirectory => Path.Combine(ClipsDirectory, "Exports");

    public static UserSettings Load()
    {
        try
        {
            var settings =
                JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(s_filePath))
                ?? new UserSettings();
            return settings.Normalized();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A Missing Or Damaged File Means Defaults
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            // A Rename Replaces The Old File Whole, Never Half Written
            Directory.CreateDirectory(AppInfo.DataDirectory);
            var temporary = s_filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, s_json));
            File.Move(temporary, s_filePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing A Setting Must Never Stop Recording
            s_logger.Warn($"Failed To Save Settings {exception.GetType().Name}");
        }
    }

    private UserSettings Normalized()
    {
        // Values From Disk Are Checked, Anything Odd Falls Back
        return this with
        {
            ReplaySeconds = ReplayChoices.Contains(ReplaySeconds) ? ReplaySeconds : 30,
            FrameRate = FrameRateChoices.Contains(FrameRate) ? FrameRate : 60,
            RemovedGames = RemovedGames?.OfType<string>().ToArray() ?? [],
            AddedGames = AddedGames?.OfType<AddedGame>().ToArray() ?? [],
            SystemAudioDeviceId = SystemAudioDeviceId ?? "",
            Hotkey = HotkeyGesture.Parse(Hotkey)?.ToString() ?? HotkeyGesture.Default.ToString(),
            ClipsFolder = IsUsableFolder(ClipsFolder) ? ClipsFolder : null,
            ExportMegabytes = SizeBudget.IsValidTarget(ExportMegabytes) ? ExportMegabytes : 10,
        };
    }

    private static bool IsUsableFolder(string? folder)
    {
        return !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder);
    }
}
