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
    public string Source => ClipNames.SourceOf(Game, Monitor);

    // Without A Given Name, A Copied File Keeps Its Own
    public string DefaultTitle =>
        Game is null && ClipNames.DateOf(Name) is null ? Name : "Untitled";

    public string Title => CustomTitle ?? DefaultTitle;

    public string? CustomTitleFrom(string text)
    {
        // Empty Or The Shown Default Stores No Name
        var typed = text.Trim();
        return typed.Length == 0 || typed == DefaultTitle ? null : typed;
    }
}
