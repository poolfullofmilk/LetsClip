using LetsClip.Data.Helpers;

namespace LetsClip.Data.ViewModels;

public sealed record ClipInfo(
    string Path,
    string Name,
    DateTimeOffset RecordedAt,
    double Duration,
    long SizeBytes,
    string? ThumbnailPath,
    string? CustomTitle,
    string? Game,
    bool HasOriginal,
    int Width,
    int Height,
    int FrameRate,
    int? Monitor,
    DateTime WrittenUtc,
    bool IsEdited
)
{
    // The Game, Else The Monitor By Its Place
    public string Source => Game ?? (Monitor is { } number ? $"Monitor {number}" : "Monitor");

    // Without A Given Name, A Copied File Keeps Its Own
    public string DefaultTitle =>
        Game is null && ClipNames.DateOf(Name) is null ? Name : "Untitled";

    public string Title => CustomTitle ?? DefaultTitle;
}
