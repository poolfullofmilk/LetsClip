using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;

namespace LetsClip.Data.Helpers;

public static class SizeBudget
{
    private const double SafetyMargin = 0.05;
    public const double AudioBitsPerSecond = 128_000;

    // A Copy Starts Up To A Second Earlier
    private const double KeyframeSeconds = 1;

    // MP4 Index And Headers, Measured On Copies
    private const int IndexBytesPerPacket = 12;
    private const int HeaderBytes = 1000;

    // A Budget's Header Reserve, Above The Measured 1000
    private const int ReservedHeaderBytes = 4096;

    // Bits Per Pixel Per Frame, Measured On Screen Captures
    private const double GoodBitsPerPixel = 0.02;
    private const double SoftBitsPerPixel = 0.008;
    private const double MinimumVideoBitsPerSecond = 250_000;

    // Lines Of The 16 By 9 Box An Encode Fits
    public const int FullLines = 1080;
    private const int MiddleLines = 720;
    private const int SmallLines = 540;

    // HEVC Keeps 1080p Down To 1.5 Mbps At 60
    private const double FullBitsPerPixel = 0.012;
    private const double MiddleBitsPerPixel = 0.005;

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

    public static bool KeepsClip(DiscordPlan plan, double seconds, double bytesPerSecond)
    {
        // Off, Or Already Small Enough, Is An Exact Copy
        return LimitOf(plan) is not { } limit
            || (seconds + KeyframeSeconds) * bytesPerSecond <= limit * 1_000_000;
    }

    private static double ContainerBytes(ExportShape clip)
    {
        // The Mp4 Index Grows With Every Video And Audio Frame
        return ReservedHeaderBytes
            + (clip.Seconds * (clip.FrameRate + (clip.HasAudio ? 47 : 0)) * IndexBytesPerPacket);
    }

    public static double SoundBytesPerSecond(PacketMap packets)
    {
        // The Sound's Packets And Their Index Entries
        return packets.AudioBytesPerSecond + (IndexBytesPerPacket * packets.AudioPacketsPerSecond);
    }

    public static long CopyBytes(PacketMap packets, TrimRange range, int tracks)
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
        return bytes
            + (IndexBytesPerPacket * count)
            + (long)Math.Round(sound, MidpointRounding.AwayFromZero)
            + HeaderBytes;
    }

    public static double LongestSeconds(double megabytes, ExportShape clip)
    {
        // The Plan Solved For Length At Its Softest Picture
        var leastVideo = Math.Max(
            MinimumVideoBitsPerSecond,
            SoftBitsPerPixel * Math.Max(1, clip.Width * clip.Height * clip.FrameRate)
        );
        var audio = clip.HasAudio ? AudioBitsPerSecond : 0;
        var index = 8 * (clip.FrameRate + (clip.HasAudio ? 47 : 0)) * IndexBytesPerPacket;
        var bits =
            ((Math.Round(megabytes * 1_000_000) * (1 - SafetyMargin)) - ReservedHeaderBytes) * 8;
        return Math.Floor(bits / (leastVideo + audio + index) * 10) / 10;
    }

    public static ExportBudget Plan(double megabytes, ExportShape clip)
    {
        var targetBytes = (long)Math.Round(megabytes * 1_000_000);
        if (clip.Seconds <= 0)
        {
            return new ExportBudget(targetBytes, 0, BudgetVerdict.TooSmall);
        }

        var videoBits = ((targetBytes * (1 - SafetyMargin)) - ContainerBytes(clip)) * 8;
        var audio = clip.HasAudio ? AudioBitsPerSecond : 0;
        var video = Math.Max(0, (videoBits / clip.Seconds) - audio);
        var bitsPerPixel = video / Math.Max(1, clip.Width * clip.Height * clip.FrameRate);
        var verdict =
            video < MinimumVideoBitsPerSecond || bitsPerPixel < SoftBitsPerPixel
                ? BudgetVerdict.TooSmall
            : bitsPerPixel < GoodBitsPerPixel ? BudgetVerdict.Soft
            : BudgetVerdict.Good;

        // Few Bits Look Better On Fewer Pixels
        var perFullPixel = video / (1920 * 1080 * Math.Max(1, clip.FrameRate));
        var lines =
            perFullPixel < MiddleBitsPerPixel ? SmallLines
            : perFullPixel < FullBitsPerPixel ? MiddleLines
            : FullLines;
        return new ExportBudget(targetBytes, video, verdict, lines);
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
            && CopyBytes(packets, new TrimRange(0.6, 0.8), 1) == 300 + (3 * 12) + 22 + HeaderBytes;

        // 20 MB Over A Minute Leaves About 2.4 Mbps
        var clip = new ExportShape(30, 1920, 1080, 60, true);
        var longest = LongestSeconds(20, clip);
        var longestHolds =
            Plan(20, clip with { Seconds = longest }).Verdict != BudgetVerdict.TooSmall
            && Plan(20, clip with { Seconds = longest + 0.2 }).Verdict == BudgetVerdict.TooSmall;
        var minute = Plan(20, clip with { Seconds = 60 });
        var silent = Plan(20, clip with { Seconds = 60, HasAudio = false });
        var totalBits = (minute.VideoBitsPerSecond + AudioBitsPerSecond) * 60;
        return copies
            && longestHolds
            && minute.TargetBytes == 20_000_000
            && totalBits < 20_000_000 * 8 * (1 - SafetyMargin)
            && minute.VideoBitsPerSecond is > 2_300_000 and < 2_500_000
            && silent.VideoBitsPerSecond - minute.VideoBitsPerSecond is > 128_000 and < 136_000
            && Plan(10, clip with { Seconds = 600 }).Verdict == BudgetVerdict.TooSmall
            && Plan(50, clip with { Seconds = 10 }).Verdict == BudgetVerdict.Good
            && Plan(50, clip with { Seconds = 10 }).Lines == FullLines
            && Plan(20, clip).Lines == FullLines
            && Plan(20, clip with { Seconds = 120 }).Lines == MiddleLines
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
            && Plan(0.5, clip with { Seconds = 10 }).Verdict == BudgetVerdict.TooSmall
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
