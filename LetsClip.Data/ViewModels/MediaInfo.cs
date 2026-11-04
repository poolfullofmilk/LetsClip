namespace LetsClip.Data.ViewModels;

public sealed record MediaInfo(
    string FormatName,
    double Duration,
    double StartTime,
    long SizeBytes,
    string? VideoCodec,
    string? PixelFormat,
    int Width,
    int Height,
    double FrameRate,
    double VideoStart,
    string? AudioCodec,
    int AudioChannels,
    double AudioStart,
    string? ColorPrimaries,
    string? ColorTransfer,
    string? ColorSpace
)
{
    public bool HasVideo => VideoCodec is not null;

    public bool HasAudio => AudioCodec is not null;
}
