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
    int Height = 0
);
