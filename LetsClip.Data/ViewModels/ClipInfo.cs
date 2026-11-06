using LetsClip.Data.Helpers;

namespace LetsClip.Data.ViewModels;

public sealed record ClipInfo(
    string Path,
    string Name,
    DateTimeOffset RecordedAt,
    double Duration,
    long SizeBytes,
    string? ThumbnailPath,
    int Width,
    int Height,
    int FrameRate,
    string? CustomTitle,
    string? Game,
    bool HasOriginal
)
{
    // Clips Without A Game Recorded The Monitor
    public const string MonitorTitle = "Monitor";

    // A Given Name, Else The Game, Else Monitor
    public string Title =>
        CustomTitle ?? Game ?? (ClipNames.DateOf(Name) is null ? Name : MonitorTitle);

    public string Details =>
        $"{Formatter.FormatDateTime(RecordedAt)} · {Formatter.FormatSize(SizeBytes)}";
}
