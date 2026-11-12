namespace LetsClip.Data.ViewModels;

public sealed record MediaInfo(
    double Duration,
    string? VideoCodec,
    string? PixelFormat,
    int Width,
    int Height,
    double FrameRate,
    string? AudioCodec,
    PacketMap? Packets
)
{
    public bool HasVideo => VideoCodec is not null;

    public bool HasAudio => AudioCodec is not null;
}
