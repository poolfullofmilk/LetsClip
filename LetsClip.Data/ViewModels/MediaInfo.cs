namespace LetsClip.Data.ViewModels;

public sealed record MediaInfo(
    double Duration,
    double StartTime,
    long SizeBytes,
    string? VideoCodec,
    string? PixelFormat,
    int Width,
    int Height,
    double FrameRate,
    double VideoStart,
    string? AudioCodec
)
{
    public bool HasVideo => VideoCodec is not null;

    public bool HasAudio => AudioCodec is not null;
}
