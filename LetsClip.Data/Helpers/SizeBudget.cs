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

    // The Largest Pick Reads Sharp Twice Over
    private const double CeilingBitsPerPixel = GoodBitsPerPixel * 2;

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
        bool hasAudio
    )
    {
        var pixels = width * (double)height * frameRate;
        var bits = seconds * ((pixels * CeilingBitsPerPixel) + (hasAudio ? AudioBitsPerSecond : 0));
        var bytes =
            ((bits / 8) + ContainerBytes(seconds, frameRate, hasAudio)) / (1 - SafetyMargin);
        return Math.Max(MinimumMegabytes + 1, Math.Ceiling(bytes / 1_000_000));
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
        double frameRate
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
            && CeilingMegabytes(30, 1920, 1080, 60, true) == 45
            && Plan(45, 30, true, 1920, 1080, 60).Verdict == BudgetVerdict.Good
            && CeilingMegabytes(1, 320, 180, 30, false) == 2
            && !IsValidTarget(double.NaN)
            && !IsValidTarget(0.5)
            && IsValidTarget(10);
    }
}
