namespace LetsClip.Data.ViewModels;

public readonly record struct ExportShape(
    double Seconds,
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio,
    double SourceBytesPerSecond
);
