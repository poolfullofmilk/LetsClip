using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;

namespace LetsClip.Data.Helpers;

public static class SizeBudget
{
    // Decimal Megabytes Everywhere, Like Upload Limits
    public const double MinimumMegabytes = 1;

    // Unlimited Asks For This, The Ceiling Then Decides
    public const double MaximumMegabytes = 4000;

    private const double SafetyMargin = 0.05;
    private const double AudioBitsPerSecond = 128_000;

    // Fixed Limits, Each Offered Only Below What The Export Needs
    private static readonly double[] s_limits = [20, 50, 100, 200, 500];

    // Bits Per Pixel Per Frame, Measured On Screen Captures
    private const double SharpBitsPerPixel = 0.045;
    private const double GoodBitsPerPixel = 0.02;
    private const double SoftBitsPerPixel = 0.008;
    private const double MinimumVideoBitsPerSecond = 250_000;

    // Sharp Twice Over, Slow Clips Counted At 30 Frames
    private const double CeilingBitsPerPixel = SharpBitsPerPixel * 2;
    private const double CeilingFrameRate = 30;

    // Half Again The Selection's Size Covers A Re-Encode
    private const double SourceHeadroom = 1.5;

    static SizeBudget()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static bool IsValidTarget(double megabytes)
    {
        return double.IsFinite(megabytes)
            && megabytes >= MinimumMegabytes
            && megabytes <= MaximumMegabytes;
    }

    public static double CeilingMegabytes(ExportShape clip)
    {
        // Past This An Export Gains Nothing
        var pixels = clip.Width * (double)clip.Height * Math.Max(clip.FrameRate, CeilingFrameRate);
        var sharp = BytesFor(clip, pixels * CeilingBitsPerPixel);
        var source =
            clip.SourceBytesPerSecond > 0
                ? clip.Seconds * clip.SourceBytesPerSecond * SourceHeadroom
                : sharp;

        // However Still The Clip, Video Keeps Twice The Floor
        var floor = BytesFor(clip, MinimumVideoBitsPerSecond * 2);
        var bytes = Math.Max(floor, Math.Min(sharp, source));
        return Math.Clamp(Math.Ceiling(bytes / 1_000_000), MinimumMegabytes + 1, MaximumMegabytes);
    }

    public static IReadOnlyList<double> LimitsFor(ExportShape clip)
    {
        // Limits The Export Can Still Meet, Then Unlimited
        var ceiling = CeilingMegabytes(clip);
        return
        [
            .. s_limits.Where(limit =>
                limit < ceiling && Plan(limit, clip).Verdict != BudgetVerdict.TooSmall
            ),
            MaximumMegabytes,
        ];
    }

    private static double BytesFor(ExportShape clip, double videoBitsPerSecond)
    {
        var bits = clip.Seconds * (videoBitsPerSecond + (clip.HasAudio ? AudioBitsPerSecond : 0));
        return ((bits / 8) + ContainerBytes(clip)) / (1 - SafetyMargin);
    }

    private static double ContainerBytes(ExportShape clip)
    {
        // The Mp4 Index Grows With Every Video And Audio Frame
        return 4096 + (clip.Seconds * (clip.FrameRate + (clip.HasAudio ? 47 : 0)) * 12);
    }

    public static ExportBudget Plan(double megabytes, ExportShape clip)
    {
        var targetBytes = (long)Math.Round(megabytes * 1_000_000);
        if (clip.Seconds <= 0 || !IsValidTarget(megabytes))
        {
            return new ExportBudget(targetBytes, 0, BudgetVerdict.TooSmall);
        }

        var videoBits = ((targetBytes * (1 - SafetyMargin)) - ContainerBytes(clip)) * 8;
        var audio = clip.HasAudio ? AudioBitsPerSecond : 0;
        var video = Math.Max(0, (videoBits / clip.Seconds) - audio);
        var bitsPerPixel = video / Math.Max(1, clip.Width * clip.Height * clip.FrameRate);

        // Room For The Whole Selection Keeps Everything There Is
        var keepsSource =
            clip.SourceBytesPerSecond > 0
            && targetBytes >= clip.Seconds * clip.SourceBytesPerSecond;
        var verdict =
            video < MinimumVideoBitsPerSecond ? BudgetVerdict.TooSmall
            : keepsSource || bitsPerPixel >= SharpBitsPerPixel ? BudgetVerdict.Sharp
            : bitsPerPixel < SoftBitsPerPixel ? BudgetVerdict.TooSmall
            : bitsPerPixel < GoodBitsPerPixel ? BudgetVerdict.Soft
            : BudgetVerdict.Good;
        return new ExportBudget(targetBytes, video, verdict);
    }

    private static bool SelfTestPasses()
    {
        // 20 MB Over A Minute Leaves About 2.4 Mbps
        var clip = new ExportShape(30, 1920, 1080, 60, true, 0);
        var minute = Plan(20, clip with { Seconds = 60 });
        var silent = Plan(20, clip with { Seconds = 60, HasAudio = false });
        var totalBits = (minute.VideoBitsPerSecond + AudioBitsPerSecond) * 60;

        // A Still Clip Needs No Limit, Long Clips Skip Some
        var still = clip with
        {
            SourceBytesPerSecond = 5_500_000 / 30.0,
        };
        return minute.TargetBytes == 20_000_000
            && totalBits < 20_000_000 * 8 * (1 - SafetyMargin)
            && minute.VideoBitsPerSecond is > 2_300_000 and < 2_500_000
            && silent.VideoBitsPerSecond - minute.VideoBitsPerSecond is > 128_000 and < 136_000
            && Plan(10, clip with { Seconds = 600 }).Verdict == BudgetVerdict.TooSmall
            && Plan(50, clip with { Seconds = 10 }).Verdict == BudgetVerdict.Sharp
            && Plan(0.5, clip with { Seconds = 10 }).Verdict == BudgetVerdict.TooSmall
            && Plan(20, clip with { Seconds = 0 }).Verdict == BudgetVerdict.TooSmall
            && Plan(9, still).Verdict == BudgetVerdict.Sharp
            && Plan(2, clip with { SourceBytesPerSecond = 1_000_000 / 30.0 }).Verdict
                == BudgetVerdict.Sharp
            && CeilingMegabytes(clip) == 45
            && CeilingMegabytes(still) == 9
            && CeilingMegabytes(clip with { FrameRate = 3 }) == 23
            && CeilingMegabytes(
                clip with
                {
                    FrameRate = 3,
                    SourceBytesPerSecond = 6_500_000 / 30.3,
                }
            ) == 10
            && CeilingMegabytes(clip with { SourceBytesPerSecond = 600_000_000 / 30.0 }) == 45
            && Plan(45, clip).Verdict == BudgetVerdict.Sharp
            && CeilingMegabytes(new ExportShape(1, 320, 180, 30, false, 0)) == 2
            && CeilingMegabytes(new ExportShape(3600, 3840, 2160, 60, true, 0)) == MaximumMegabytes
            && LimitsFor(still).SequenceEqual([MaximumMegabytes])
            && LimitsFor(clip).SequenceEqual([20, MaximumMegabytes])
            && LimitsFor(clip with { Seconds = 600 })
                .SequenceEqual([100, 200, 500, MaximumMegabytes])
            && !IsValidTarget(double.NaN)
            && !IsValidTarget(0.5)
            && IsValidTarget(10);
    }
}
