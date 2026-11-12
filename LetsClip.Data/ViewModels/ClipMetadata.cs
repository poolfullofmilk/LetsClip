using LetsClip.Data.Helpers;

namespace LetsClip.Data.ViewModels;

public sealed record ClipMetadata(
    DateTimeOffset RecordedAt,
    double Duration,
    double? TrimStart,
    double? TrimEnd,
    long FileSize,
    DateTime FileWriteTimeUtc,
    int? FrameRate = null,
    string? Title = null,
    string? Game = null,
    int Width = 0,
    int Height = 0,
    double? CutStart = null,
    double? CutEnd = null,
    bool ForeignOriginal = false,
    int? Monitor = null,
    bool Cut = false
)
{
    // Named, Cut Or Trimmed Short, Only Unedited Leaves It Out
    public bool IsEdited =>
        Title is not null
        || Cut
        || (
            TrimStart is { } start
            && TrimEnd is { } end
            && new TrimRange(start, end).CutsShort(Duration)
        );
}
