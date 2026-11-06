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
    // A Name Given In LetsClip, Else A Generated File's Date
    public string Title =>
        CustomTitle
        ?? (ClipNames.DateOf(Name) is not null ? Formatter.FormatDateTime(RecordedAt) : Name);

    // A Title Other Than The Date Keeps The Date Below
    public bool ShowsDate => CustomTitle is not null || ClipNames.DateOf(Name) is null;

    public string Details =>
        ShowsDate
            ? $"{Formatter.FormatDateTime(RecordedAt)} · {Formatter.FormatSize(SizeBytes)}"
            : Formatter.FormatSize(SizeBytes);
}
