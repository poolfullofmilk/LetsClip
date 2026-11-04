using LetsClip.Data.Helpers;

namespace LetsClip.Data.ViewModels;

public sealed record ClipInfo(
    string Path,
    string Name,
    DateTimeOffset RecordedAt,
    double Duration,
    long SizeBytes,
    string? ThumbnailPath,
    bool HasAudio,
    int Width,
    int Height,
    double FrameRate
)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    // A Dated Title Leaves Only The Size Below It
    public string Title =>
        ClipNames.IsGenerated(Name) ? Formatter.FormatDateTime(RecordedAt) : Name;

    public string Details =>
        ClipNames.IsGenerated(Name)
            ? Formatter.FormatSize(SizeBytes)
            : $"{Formatter.FormatDateTime(RecordedAt)} · {Formatter.FormatSize(SizeBytes)}";
}
