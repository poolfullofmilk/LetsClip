using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Media;

public sealed record ExportRequest(
    string SourcePath,
    TrimRange Range,
    DiscordPlan Plan,
    double FrameRate,
    int? AudioTrack,
    string ExportsFolder,
    bool CopyWhenDone = false,
    double? Crop = null,
    bool Video = true
);

public sealed record ExportProgress(string Phase, double Fraction);

public sealed class ExportException(string message) : Exception(message);

// Neither Means The Clip Itself Already Fits
public sealed record DiscordCopy(ExportRequest? Request, string? Problem);

public static partial class ClipExporter
{
    // A Shorter Result Means Part Of The Selection Was Lost
    private const double DurationTolerance = 0.25;

    // A Third Try Follows The Line The First Two Drew
    private const int AttemptsPerEncoder = 3;

    // Single Pass AMF Overshot Its Average By 5 To 8%
    private const double HardwareAim = 0.9;

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_stall = TimeSpan.FromSeconds(15);

    // AMF Shows Progress Within A Second, A Silent Start Hung
    private static readonly TimeSpan s_hardwareStart = TimeSpan.FromSeconds(5);

    private const string TooSmallMessage =
        "Too long for this Discord plan, pick a shorter selection";

    private const string RejectedMessage = "The exported file did not check out, nothing was kept";

    // Encoded Sound Lands Near Streaming Loudness, Peaks Kept Under
    private const double TargetLoudness = -14;
    private const double MostBoost = 10;
    private const double PeakCeiling = -3;
    private const double MostLimiting = 6;
    private const string PeakLimit = "alimiter=limit=0.708:level=0:latency=1";

    static ClipExporter()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static (int Width, int Height) OutputSize(
        int width,
        int height,
        int lines = SizeBudget.FullLines
    )
    {
        // A 16 By 9 Box In Its Orientation, Never Upscaled
        var portrait = height > width;
        var across = lines * 16 / 9.0;
        var scale = Math.Min(
            1,
            Math.Min((portrait ? lines : across) / width, (portrait ? across : lines) / height)
        );
        return (EvenUp(width * scale), EvenUp(height * scale));
    }

    internal static double NextBitrate(
        IReadOnlyList<(double Bitrate, long Size)> tries,
        long target
    )
    {
        // The Miss Plus Margin, Then Along Two Tries' Line
        var (bitrate, size) = tries[^1];
        var proportional = bitrate * target / size * 0.97;
        if (tries.Count < 2)
        {
            return proportional;
        }

        // Quick Sync Barely Shrank At Lower Bitrates, Step Harder
        var (earlierBitrate, earlierSize) = tries[^2];
        var bytesPerBit = (size - earlierSize) / (bitrate - earlierBitrate);
        var aimed =
            bytesPerBit > 0 ? bitrate - ((size - (target * 0.97)) / bytesPerBit) : bitrate / 2;
        return Math.Clamp(aimed, bitrate / 2, proportional);
    }

    public static (int Width, int Height) CropSizeOf(int height)
    {
        // The Tallest 9 By 16 Part Of The Frame, Even
        return ((int)(height * 9 / 16.0) / 2 * 2, height / 2 * 2);
    }

    public static long VerticalBytes(
        long clipBytes,
        double clipSeconds,
        MediaInfo media,
        double seconds
    )
    {
        // Off Keeps The Clip's Bits Per Pixel Over The Crop
        var (width, height) = CropSizeOf(media.Height);
        var kept = width * height / (double)(media.Width * media.Height);
        return (long)(clipBytes / clipSeconds * seconds * kept);
    }

    public static double FrameRateOf(MediaInfo media, bool encodes)
    {
        // Odd Probes Read As 60, An Encode Stops At 60
        var rate = media.FrameRate is > 1 and < 241 ? Math.Round(media.FrameRate) : 60;
        return encodes ? Math.Min(rate, 60) : rate;
    }

    public static int? TrackOf(MediaInfo media, bool game, bool microphone)
    {
        // Separate Tracks Can Leave Out Game Or Voice
        var keepsGame = game && media.HasAudio;
        return media.HasSeparateTracks ? AudioTracks.IndexOf(keepsGame, microphone)
            : keepsGame ? 0
            : null;
    }

    public static bool KeepsVideo(MediaInfo media, bool video, bool game, bool microphone)
    {
        // Without Sound Kept, The Video Stays, Never Nothing
        return video || TrackOf(media, game, microphone) is null;
    }

    public static long CopyBytes(
        long clipBytes,
        MediaInfo media,
        TrimRange range,
        int tracks,
        bool video = true
    )
    {
        // The Whole Clip Is The File, A Part Never More
        if (video && !range.CutsShort(media.Duration) && tracks == media.AudioTrackCount)
        {
            return clipBytes;
        }

        // Without Packets, A Share Of The File Or Usual Sound
        var estimate = video
            ? clipBytes * range.Length / media.Duration
            : tracks * range.Length * SizeBudget.AudioBitsPerSecond / 8;
        return Math.Min(
            clipBytes,
            media.Packets is { } packets
                ? SizeBudget.CopyBytes(packets, range, tracks, video)
                : (long)estimate
        );
    }

    public static DiscordCopy ForDiscord(
        ClipInfo clip,
        MediaInfo media,
        TrimRange range,
        DiscordPlan plan,
        bool game,
        bool microphone,
        bool video,
        string folder,
        double? crop
    )
    {
        // The Editor's Plan Rules, Pressed From A Card, Crop Included
        var track = TrackOf(media, game, microphone);
        var keepsVideo = KeepsVideo(media, video, game, microphone);
        var limit = SizeBudget.LimitOf(plan) ?? double.MaxValue;
        var cropped = keepsVideo && crop is not null && media.Width > media.Height;
        var copied = CopyBytes(clip.SizeBytes, media, range, track is null ? 0 : 1, keepsVideo);

        // Sound Alone Becomes Opus, Which Discord Plays Inline
        var encodes = cropped || !keepsVideo || copied > limit * 1e6;
        var everyTrack = media.HasSeparateTracks ? track == 0 : track is not null == media.HasAudio;
        if (!encodes && keepsVideo && everyTrack && !range.CutsShort(media.Duration))
        {
            return new(null, null);
        }

        var frameRate = FrameRateOf(media, encodes);
        var (frameWidth, frameHeight) = cropped
            ? CropSizeOf(media.Height)
            : (media.Width, media.Height);
        var (width, height) = OutputSize(frameWidth, frameHeight);
        var shape = new ExportShape(
            range.Length,
            width,
            height,
            frameRate,
            track is not null,
            keepsVideo
        );
        if (encodes && SizeBudget.Plan(limit, shape).Verdict == BudgetVerdict.TooSmall)
        {
            return new(
                null,
                $"Too long for {SizeBudget.NameOf(plan)}, which fits about {Formatter.FormatDuration(SizeBudget.LongestSeconds(limit, shape))}, open it to pick a shorter selection"
            );
        }

        return new(
            new ExportRequest(
                clip.Path,
                range,
                encodes ? plan : DiscordPlan.Off,
                frameRate,
                track,
                folder,
                CopyWhenDone: true,
                Crop: cropped ? crop : null,
                Video: keepsVideo
            ),
            null
        );
    }

    private static bool SelfTestPasses()
    {
        // Quick Sync's Measured Miss, 51.4 Then 50.7 MB For 47.5
        var first = NextBitrate([(10_000_000, 51_400_000)], 47_500_000);
        var second = NextBitrate([(10_000_000, 51_400_000), (first, 50_700_000)], 47_500_000);
        return Math.Abs(first - (10_000_000 * 47.5 / 51.4 * 0.97)) < 1
            && second < first * 0.75
            && second >= first / 2
            && NextBitrate([(10_000_000, 30_000_000), (9_000_000, 31_000_000)], 20_000_000)
                == 4_500_000
            && FrameRateOf(new MediaInfo(5, "h264", null, 0, 0, 239.9, null, null), true) == 60
            && FrameRateOf(new MediaInfo(5, "h264", null, 0, 0, 0, null, null), false) == 60;
    }
}
