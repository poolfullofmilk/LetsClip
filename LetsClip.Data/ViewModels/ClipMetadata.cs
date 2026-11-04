namespace LetsClip.Data.ViewModels;

public sealed record ClipMetadata(
    DateTimeOffset RecordedAt,
    string? MonitorName,
    double FileDuration,
    double KeepStart,
    double KeepEnd,
    double? TrimStart,
    double? TrimEnd,
    bool HasAudio,
    int Width,
    int Height,
    double FrameRate,
    long FileSize,
    DateTime FileWriteTimeUtc
);
