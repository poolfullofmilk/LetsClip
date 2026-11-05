using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;

namespace LetsClip.Data.Helpers;

public static class SizeBudget
{
    // Decimal Megabytes Everywhere, Like Upload Limits
    public const double SafetyMargin = 0.05;
    public const double AudioBitsPerSecond = 128_000;
    public const double MinimumMegabytes = 1;

    // Unlimited Asks For This, The Ceiling Then Decides
    public const double MaximumMegabytes = 4000;

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

    public static long BytesOf(double megabytes)
    {
        return (long)Math.Round(megabytes * 1_000_000);
    }

    public static bool IsValidTarget(double megabytes)
    {
        return double.IsFinite(megabytes)
            && megabytes >= MinimumMegabytes
            && megabytes <= MaximumMegabytes;
    }

    public static double CeilingMegabytes(
        double seconds,
        int width,
        int height,
        double frameRate,
        bool hasAudio,
        double sourceBytesPerSecond
    )
    {
        // Past This An Export Gains Nothing
        var pixels = width * (double)height * Math.Max(frameRate, CeilingFrameRate);
        var sharp = BytesFor(seconds, pixels * CeilingBitsPerPixel, frameRate, hasAudio);
        var source =
            sourceBytesPerSecond > 0 ? seconds * sourceBytesPerSecond * SourceHeadroom : sharp;

        // However Still The Clip, Video Keeps Twice The Floor
        var floor = BytesFor(seconds, MinimumVideoBitsPerSecond * 2, frameRate, hasAudio);
        var bytes = Math.Max(floor, Math.Min(sharp, source));
        return Math.Clamp(Math.Ceiling(bytes / 1_000_000), MinimumMegabytes + 1, MaximumMegabytes);
    }

    public static IReadOnlyList<double> LimitsFor(
        double seconds,
        int width,
        int height,
        double frameRate,
        bool hasAudio,
        double sourceBytesPerSecond
    )
    {
        // Limits The Export Can Still Meet, Then Unlimited
        var ceiling = CeilingMegabytes(
            seconds,
            width,
            height,
            frameRate,
            hasAudio,
            sourceBytesPerSecond
        );
        return
        [
            .. s_limits.Where(limit =>
                limit < ceiling
                && Plan(
                    limit,
                    seconds,
                    hasAudio,
                    width,
                    height,
                    frameRate,
                    sourceBytesPerSecond
                ).Verdict != BudgetVerdict.TooSmall
            ),
            MaximumMegabytes,
        ];
    }

    private static double BytesFor(
        double seconds,
        double videoBitsPerSecond,
        double frameRate,
        bool hasAudio
    )
    {
        var bits = seconds * (videoBitsPerSecond + (hasAudio ? AudioBitsPerSecond : 0));
        return ((bits / 8) + ContainerBytes(seconds, frameRate, hasAudio)) / (1 - SafetyMargin);
    }

    private static double ContainerBytes(double seconds, double frameRate, bool hasAudio)
    {
        // The Mp4 Index Grows With Every Video And Audio Frame
        return 4096 + (seconds * (frameRate + (hasAudio ? 47 : 0)) * 12);
    }

    public static ExportBudget Plan(
        double megabytes,
        double seconds,
        bool hasAudio,
        int width,
        int height,
        double frameRate,
        double sourceBytesPerSecond
    )
    {
        var targetBytes = BytesOf(megabytes);
        var audio = hasAudio ? AudioBitsPerSecond : 0;
        if (seconds <= 0 || !IsValidTarget(megabytes))
        {
            return new ExportBudget(targetBytes, 0, audio, BudgetVerdict.TooSmall);
        }

        var videoBits =
            ((targetBytes * (1 - SafetyMargin)) - ContainerBytes(seconds, frameRate, hasAudio)) * 8;
        var video = Math.Max(0, (videoBits / seconds) - audio);
        var bitsPerPixel = video / Math.Max(1, width * height * frameRate);

        // Room For The Whole Selection Keeps Everything There Is
        var keepsSource = sourceBytesPerSecond > 0 && targetBytes >= seconds * sourceBytesPerSecond;
        var verdict =
            video < MinimumVideoBitsPerSecond ? BudgetVerdict.TooSmall
            : keepsSource || bitsPerPixel >= SharpBitsPerPixel ? BudgetVerdict.Sharp
            : bitsPerPixel < SoftBitsPerPixel ? BudgetVerdict.TooSmall
            : bitsPerPixel < GoodBitsPerPixel ? BudgetVerdict.Soft
            : BudgetVerdict.Good;
        return new ExportBudget(targetBytes, video, audio, verdict);
    }

    private static bool SelfTestPasses()
    {
        // 20 MB Over A Minute Leaves About 2.4 Mbps
        var minute = Plan(20, 60, true, 1920, 1080, 60, 0);
        var silent = Plan(20, 60, false, 1920, 1080, 60, 0);
        var totalBits = (minute.VideoBitsPerSecond + minute.AudioBitsPerSecond) * 60;

        // A Still Clip Needs No Limit, Long Clips Skip Some
        var still = 5_500_000 / 30.0;
        return minute.TargetBytes == 20_000_000
            && totalBits < 20_000_000 * 8 * (1 - SafetyMargin)
            && minute.VideoBitsPerSecond is > 2_300_000 and < 2_500_000
            && silent.VideoBitsPerSecond - minute.VideoBitsPerSecond is > 128_000 and < 136_000
            && Plan(10, 600, true, 1920, 1080, 60, 0).Verdict == BudgetVerdict.TooSmall
            && Plan(50, 10, true, 1920, 1080, 60, 0).Verdict == BudgetVerdict.Sharp
            && Plan(0.5, 10, true, 1920, 1080, 60, 0).Verdict == BudgetVerdict.TooSmall
            && Plan(20, 0, true, 1920, 1080, 60, 0).Verdict == BudgetVerdict.TooSmall
            && Plan(9, 30, true, 1920, 1080, 60, still).Verdict == BudgetVerdict.Sharp
            && Plan(2, 30, true, 1920, 1080, 60, 1_000_000 / 30.0).Verdict == BudgetVerdict.Sharp
            && CeilingMegabytes(30, 1920, 1080, 60, true, 0) == 45
            && CeilingMegabytes(30, 1920, 1080, 60, true, still) == 9
            && CeilingMegabytes(30, 1920, 1080, 3, true, 0) == 23
            && CeilingMegabytes(30, 1920, 1080, 3, true, 6_500_000 / 30.3) == 10
            && CeilingMegabytes(30, 1920, 1080, 60, true, 600_000_000 / 30.0) == 45
            && Plan(45, 30, true, 1920, 1080, 60, 0).Verdict == BudgetVerdict.Sharp
            && CeilingMegabytes(1, 320, 180, 30, false, 0) == 2
            && CeilingMegabytes(3600, 3840, 2160, 60, true, 0) == MaximumMegabytes
            && LimitsFor(30, 1920, 1080, 60, true, still).SequenceEqual([MaximumMegabytes])
            && LimitsFor(30, 1920, 1080, 60, true, 0).SequenceEqual([20, MaximumMegabytes])
            && LimitsFor(600, 1920, 1080, 60, true, 0)
                .SequenceEqual([100, 200, 500, MaximumMegabytes])
            && !IsValidTarget(double.NaN)
            && !IsValidTarget(0.5)
            && IsValidTarget(10);
    }
}
