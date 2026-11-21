using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;

namespace LetsClip.Data.Helpers;

public static class SizeBudget
{
    private const double SafetyMargin = 0.05;
    public const double AudioBitsPerSecond = 128_000;

    // Sound Takes A Fifth Of The Bits, Never Under This
    private const double LeastAudioBitsPerSecond = 24_000;
    private const int AudioShare = 5;

    // Below This, One Channel Keeps Voices Clear
    public const double StereoBitsPerSecond = 48_000;

    // AAC At 48 kHz Writes About 47 Packets A Second
    private const int AudioPacketsPerSecond = 47;

    // A Copy Starts Up To A Second Earlier
    private const double KeyframeSeconds = 1;

    // MP4 Index And Headers, Measured On Copies
    private const int IndexBytesPerPacket = 12;
    private const int HeaderBytes = 1000;

    // A Budget's Header Reserve, Above The Measured 1000
    private const int ReservedHeaderBytes = 4096;

    // Bits Per Pixel Per Frame Of The Clip's Own Shape
    private const double GoodBitsPerPixel = 0.02;

    // Lines Of The 16 By 9 Box An Encode Fits
    public const int FullLines = 1080;

    // Each Rung Sits Just Above AMF HEVC's Measured Floor
    private static readonly (int Lines, double FrameRate, double LeastBitsPerPixel)[] s_rungs =
    [
        (FullLines, 60, 0.012),
        (720, 60, 0.01125),
        (540, 60, 0.0135),
        (360, 60, 0.017),
        (240, 60, 0.02),
        (240, 30, 0.028),
        (240, 15, 0.042),
    ];

    static SizeBudget()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static double? LimitOf(DiscordPlan plan)
    {
        // Decimal Megabytes, Safe For Either Reading Of Discord's Limit
        return plan switch
        {
            DiscordPlan.Free => 20,
            DiscordPlan.NitroBasic => 50,
            DiscordPlan.Nitro => 1000,
            _ => null,
        };
    }

    public static string NameOf(DiscordPlan plan)
    {
        return plan switch
        {
            DiscordPlan.Free => "Free",
            DiscordPlan.NitroBasic => "Nitro Basic",
            DiscordPlan.Nitro => "Nitro",
            _ => "Off",
        };
    }

    public static string? LimitTextOf(DiscordPlan plan)
    {
        return LimitOf(plan) is { } limit ? Formatter.FormatSize((long)(limit * 1_000_000)) : null;
    }

    public static bool KeepsClip(DiscordPlan plan, double seconds, double bytesPerSecond)
    {
        // Off, Or Already Small Enough, Is An Exact Copy
        return LimitOf(plan) is not { } limit
            || (seconds + KeyframeSeconds) * bytesPerSecond <= limit * 1_000_000;
    }

    private static double IndexBitsPerSecond(double frameRate, bool hasAudio)
    {
        // The Mp4 Index Grows With Every Video And Audio Frame
        return 8 * IndexBytesPerPacket * (frameRate + (hasAudio ? AudioPacketsPerSecond : 0));
    }

    private static double SoundBytesPerSecond(PacketMap packets)
    {
        // The Sound's Packets And Their Index Entries
        return packets.AudioBytesPerSecond + (IndexBytesPerPacket * packets.AudioPacketsPerSecond);
    }

    public static long CopyBytes(PacketMap packets, TrimRange range, int tracks, bool video = true)
    {
        // A Copy Runs From The Keyframe Before Its Seek
        var times = packets.Times;
        var seek = range.SeekStart;
        var stop = seek + range.Length;
        var first = 0;
        foreach (var key in packets.Keys.TakeWhile(key => times[key] <= seek))
        {
            first = key;
        }

        long bytes = 0;
        var count = 0;
        for (var index = first; index < times.Length && times[index] < stop; index++)
        {
            bytes += packets.Sizes[index];
            count++;
        }

        // The Sound Is Copied From That Keyframe On Too
        var lead = times.Length > 0 ? Math.Max(0, seek - times[first]) : 0;

        // The Page Script Weighs The Same, Rounding The Same Way
        var sound = tracks * (range.Length + lead) * SoundBytesPerSecond(packets);
        var pictures = video ? bytes + (IndexBytesPerPacket * count) : 0;
        return pictures + (long)Math.Round(sound, MidpointRounding.AwayFromZero) + HeaderBytes;
    }

    private static double UsableBits(double megabytes)
    {
        return ((Math.Round(megabytes * 1_000_000) * (1 - SafetyMargin)) - ReservedHeaderBytes) * 8;
    }

    private static double LeastVideoOf(
        (int Lines, double FrameRate, double LeastBitsPerPixel) rung,
        double frameRate
    )
    {
        // The Rung's 16 By 9 Box At Its Frame Rate
        return rung.LeastBitsPerPixel
            * Math.Round(rung.Lines * 16 / 9.0)
            * rung.Lines
            * Math.Min(rung.FrameRate, frameRate);
    }

    public static double KeepingMegabytes(ExportShape clip)
    {
        // The Smallest Rung Holding The Frame, And A Good Picture
        var frameRate = clip.HasVideo ? clip.FrameRate : 0;
        var (shortSide, longSide) = (
            Math.Min(clip.Width, clip.Height),
            Math.Max(clip.Width, clip.Height)
        );
        var holding = s_rungs.Last(rung =>
            rung == s_rungs[0]
            || (
                rung.FrameRate >= frameRate
                && shortSide <= rung.Lines
                && longSide <= rung.Lines * 16 / 9.0
            )
        );
        var video = clip.HasVideo
            ? Math.Max(
                GoodBitsPerPixel * clip.Width * clip.Height * frameRate,
                LeastVideoOf(holding, frameRate)
            )
            : 0;
        var audio = clip.HasAudio ? AudioBitsPerSecond : 0;
        var bits = (video + audio + IndexBitsPerSecond(frameRate, clip.HasAudio)) * clip.Seconds;
        return Math.Ceiling(((bits / 8) + ReservedHeaderBytes) / (1 - SafetyMargin)) / 1_000_000;
    }

    public static double LongestSeconds(double megabytes, ExportShape clip)
    {
        // The Plan Solved For Length At Its Lowest Rung
        var floor = s_rungs[^1];
        var frameRate = clip.HasVideo ? Math.Min(floor.FrameRate, clip.FrameRate) : 0;
        var video = clip.HasVideo ? LeastVideoOf(floor, clip.FrameRate) : 0;
        var audio = clip.HasAudio ? LeastAudioBitsPerSecond : 0;
        var index = IndexBitsPerSecond(frameRate, clip.HasAudio);
        return Math.Floor(UsableBits(megabytes) / (video + audio + index) * 10) / 10;
    }

    public static ExportBudget Plan(double megabytes, ExportShape clip)
    {
        var targetBytes = (long)Math.Round(megabytes * 1_000_000);
        if (clip.Seconds <= 0)
        {
            return new ExportBudget(targetBytes, 0, BudgetVerdict.TooSmall);
        }

        // Sound Alone Takes Everything, Beside A Picture A Fifth
        var usable = UsableBits(megabytes) / clip.Seconds;
        var frameRate = clip.HasVideo ? clip.FrameRate : 0;
        var bits = usable - IndexBitsPerSecond(frameRate, clip.HasAudio);
        var audio =
            !clip.HasAudio ? 0
            : !clip.HasVideo ? Math.Min(AudioBitsPerSecond, bits)
            : Math.Clamp(bits / AudioShare, LeastAudioBitsPerSecond, AudioBitsPerSecond);
        if (!clip.HasVideo)
        {
            return new ExportBudget(
                targetBytes,
                0,
                audio < LeastAudioBitsPerSecond ? BudgetVerdict.TooSmall : BudgetVerdict.Good,
                AudioBitsPerSecond: audio
            );
        }

        // Few Bits Look Better On Fewer Pixels And Frames
        double VideoAt(double rate) => usable - IndexBitsPerSecond(rate, clip.HasAudio) - audio;
        var picked = s_rungs.FirstOrDefault(
            rung => VideoAt(Math.Min(rung.FrameRate, frameRate)) >= LeastVideoOf(rung, frameRate),
            s_rungs[^1]
        );
        var rate = Math.Min(picked.FrameRate, frameRate);
        var video = VideoAt(rate);
        var fits = video >= LeastVideoOf(picked, frameRate);
        var bitsPerPixel = video / Math.Max(1, clip.Width * clip.Height * frameRate);
        var verdict =
            !fits || audio < LeastAudioBitsPerSecond ? BudgetVerdict.TooSmall
            : bitsPerPixel < GoodBitsPerPixel ? BudgetVerdict.Soft
            : BudgetVerdict.Good;
        return new ExportBudget(
            targetBytes,
            Math.Max(0, video),
            verdict,
            picked.Lines,
            rate,
            audio
        );
    }

    private static bool SelfTestPasses()
    {
        // Ten 100 Byte Frames, Keyframes At 0 And 0.5
        var packets = new PacketMap(
            [.. Enumerable.Range(0, 10).Select(index => index / 10.0)],
            [.. Enumerable.Repeat(100L, 10)],
            [0, 5],
            50,
            2
        );
        var copies =
            CopyBytes(packets, new TrimRange(0.6, 0.8), 0) == 300 + (3 * 12) + HeaderBytes
            && CopyBytes(packets, new TrimRange(0.5, 0.8), 0) == 800 + (8 * 12) + HeaderBytes
            && CopyBytes(packets, new TrimRange(0, 1), 1)
                == 1000 + (10 * 12) + 50 + 24 + HeaderBytes
            && CopyBytes(packets, new TrimRange(0.6, 0.8), 1) == 300 + (3 * 12) + 22 + HeaderBytes
            && CopyBytes(packets, new TrimRange(0.6, 0.8), 1, video: false) == 22 + HeaderBytes;

        // 20 MB Over A Minute Leaves About 2.4 Mbps
        var clip = new ExportShape(30, 1920, 1080, 60, true);
        var longest = LongestSeconds(20, clip);
        var longestHolds =
            Plan(20, clip with { Seconds = longest }).Verdict != BudgetVerdict.TooSmall
            && Plan(20, clip with { Seconds = longest + 0.2 }).Verdict == BudgetVerdict.TooSmall;
        var sound = clip with { HasVideo = false };
        var soundLongest = LongestSeconds(20, sound);
        var soundHolds =
            Plan(20, sound with { Seconds = soundLongest }).Verdict == BudgetVerdict.Good
            && Plan(20, sound with { Seconds = soundLongest + 0.2 }).Verdict
                == BudgetVerdict.TooSmall
            && Plan(20, sound with { Seconds = 60 }).AudioBitsPerSecond == AudioBitsPerSecond;
        var minute = Plan(20, clip with { Seconds = 60 });
        var silent = Plan(20, clip with { Seconds = 60, HasAudio = false });
        var totalBits = (minute.VideoBitsPerSecond + minute.AudioBitsPerSecond) * 60;
        var tenMinutes = Plan(20, clip with { Seconds = 600 });
        var small = new ExportShape(30, 600, 400, 60, true);
        var kept = Plan(KeepingMegabytes(small), small);
        var square = new ExportShape(30, 1080, 1080, 60, true);
        return copies
            && kept is { Verdict: BudgetVerdict.Good, Lines: 540, FrameRate: 60 }
            && Plan(KeepingMegabytes(square), square)
                is { Verdict: BudgetVerdict.Good, Lines: FullLines }
            && KeepingMegabytes(new ExportShape(30, 192, 166, 60, true)) < 2
            && longestHolds
            && soundHolds
            && longest > 1200
            && soundLongest > longest
            && minute.TargetBytes == 20_000_000
            && totalBits < 20_000_000 * 8 * (1 - SafetyMargin)
            && minute.VideoBitsPerSecond is > 2_300_000 and < 2_500_000
            && minute.AudioBitsPerSecond == AudioBitsPerSecond
            && silent.VideoBitsPerSecond - minute.VideoBitsPerSecond is > 128_000 and < 136_000
            && tenMinutes.Verdict == BudgetVerdict.Soft
            && tenMinutes.Lines < 540
            && tenMinutes.AudioBitsPerSecond < AudioBitsPerSecond
            && Plan(50, clip with { Seconds = 10 }).Verdict == BudgetVerdict.Good
            && Plan(50, clip with { Seconds = 10 }).Lines == FullLines
            && Plan(20, clip).Lines == FullLines
            && Plan(20, clip with { Seconds = 120 }).Lines == 720
            && Plan(
                20,
                clip with
                {
                    Seconds = 120,
                    Width = 1280,
                    Height = 720,
                    FrameRate = 30,
                }
            ).Lines == FullLines
            && Plan(0.05, clip with { Seconds = 10 }).Verdict == BudgetVerdict.TooSmall
            && Plan(20, clip with { Seconds = 0 }).Verdict == BudgetVerdict.TooSmall
            && LimitOf(DiscordPlan.Off) is null
            && LimitOf(DiscordPlan.Free) == 20
            && LimitOf(DiscordPlan.NitroBasic) == 50
            && LimitOf(DiscordPlan.Nitro) == 1000
            && KeepsClip(DiscordPlan.Off, 3600, 40_000_000)
            && KeepsClip(DiscordPlan.Free, 10, 1_000_000)
            && !KeepsClip(DiscordPlan.Free, 30, 1_000_000)
            && KeepsClip(DiscordPlan.NitroBasic, 30, 1_000_000)
            && !KeepsClip(DiscordPlan.NitroBasic, 60, 1_000_000)
            && KeepsClip(DiscordPlan.Nitro, 60, 1_000_000);
    }
}
