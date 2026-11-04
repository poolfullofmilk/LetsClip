using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;

namespace LetsClip.Data.Helpers;

public static class SizeBudget
{
    // Decimal Megabytes Everywhere, Like Discord's Own Limits
    public const double SafetyMargin = 0.05;
    public const double AudioBitsPerSecond = 128_000;
    public const double MinimumMegabytes = 1;
    public const double MaximumMegabytes = 4000;

    // Bits Per Pixel Per Frame, Measured On Screen Captures
    private const double GoodBitsPerPixel = 0.045;
    private const double FairBitsPerPixel = 0.02;
    private const double LowBitsPerPixel = 0.008;
    private const double MinimumVideoBitsPerSecond = 250_000;

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

    public static double CeilingMegabytes(long sourceBytes, double seconds, double sourceDuration)
    {
        // The Selection's Share Of The Clip, More Adds Nothing
        var share = sourceDuration > 0 ? Math.Clamp(seconds / sourceDuration, 0, 1) : 1;
        return Math.Max(MinimumMegabytes + 1, Math.Ceiling(sourceBytes * share / 1_000_000));
    }

    public static ExportBudget Plan(
        double megabytes,
        double seconds,
        bool hasAudio,
        int width,
        int height,
        double frameRate
    )
    {
        var targetBytes = BytesOf(megabytes);
        var audio = hasAudio ? AudioBitsPerSecond : 0;
        if (seconds <= 0 || !IsValidTarget(megabytes))
        {
            return new ExportBudget(targetBytes, 0, audio, BudgetVerdict.TooSmall);
        }

        // The Mp4 Index Grows With Every Video And Audio Frame
        var containerBytes = 4096 + seconds * (frameRate + (hasAudio ? 47 : 0)) * 12;
        var videoBits = ((targetBytes * (1 - SafetyMargin)) - containerBytes) * 8;
        var video = Math.Max(0, (videoBits / seconds) - audio);
        var bitsPerPixel = video / Math.Max(1, width * height * frameRate);
        var verdict =
            video < MinimumVideoBitsPerSecond || bitsPerPixel < LowBitsPerPixel
                ? BudgetVerdict.TooSmall
            : bitsPerPixel < FairBitsPerPixel ? BudgetVerdict.Low
            : bitsPerPixel < GoodBitsPerPixel ? BudgetVerdict.Fair
            : BudgetVerdict.Good;
        return new ExportBudget(targetBytes, video, audio, verdict);
    }

    private static bool SelfTestPasses()
    {
        // 20 MB Over A Minute Leaves About 2.4 Mbps
        var minute = Plan(20, 60, true, 1920, 1080, 60);
        var silent = Plan(20, 60, false, 1920, 1080, 60);
        var totalBits = (minute.VideoBitsPerSecond + minute.AudioBitsPerSecond) * 60;
        return minute.TargetBytes == 20_000_000
            && totalBits < 20_000_000 * 8 * (1 - SafetyMargin)
            && minute.VideoBitsPerSecond is > 2_300_000 and < 2_500_000
            && silent.VideoBitsPerSecond - minute.VideoBitsPerSecond is > 128_000 and < 136_000
            && Plan(10, 600, true, 1920, 1080, 60).Verdict == BudgetVerdict.TooSmall
            && Plan(50, 10, true, 1920, 1080, 60).Verdict == BudgetVerdict.Good
            && Plan(0.5, 10, true, 1920, 1080, 60).Verdict == BudgetVerdict.TooSmall
            && Plan(20, 0, true, 1920, 1080, 60).Verdict == BudgetVerdict.TooSmall
            && CeilingMegabytes(30_000_000, 15, 30) == 15
            && CeilingMegabytes(30_000_000, 15.1, 30) == 16
            && CeilingMegabytes(100_000, 1, 30) == 2
            && !IsValidTarget(double.NaN)
            && !IsValidTarget(0.5)
            && IsValidTarget(10);
    }
}
