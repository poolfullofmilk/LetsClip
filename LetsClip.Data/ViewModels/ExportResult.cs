namespace LetsClip.Data.ViewModels;

public sealed record ExportResult(
    string Path,
    long SizeBytes,
    double Duration,
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio
);
