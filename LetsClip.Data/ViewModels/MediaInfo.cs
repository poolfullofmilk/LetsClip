namespace LetsClip.Data.ViewModels;

public sealed record MediaInfo(
    double Duration,
    string? VideoCodec,
    string? PixelFormat,
    int Width,
    int Height,
    double FrameRate,
    string? AudioCodec,
    PacketMap? Packets,
    int AudioTrackCount = 0,
    string[]? Stems = null,
    string? AudioName = null
)
{
    public bool HasVideo => VideoCodec is not null;

    public bool HasAudio => AudioCodec is not null;

    // Game, Microphone Or Voice Chat Apart, Each Beside The Mix
    public IReadOnlyList<string> Parts => Stems ?? [];

    public bool HasSeparateTracks => Parts.Count > 0;

    public int? TrackOf(string stem)
    {
        // The Mix Plays First, Each Part Follows In Its Order
        var index = Parts.ToList().IndexOf(stem);
        return index < 0 ? null : index + 1;
    }
}
