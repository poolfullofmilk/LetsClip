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

    // Bits Per Pixel Per Frame, Measured On Screen Captures
    private const double GoodBitsPerPixel = 0.02;
    private const double SoftBitsPerPixel = 0.008;
    private const double MinimumVideoBitsPerSecond = 250_000;

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
            DiscordPlan.Nitro => 500,
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
        return 4096 + (clip.Seconds * (clip.FrameRate + (clip.HasAudio ? 47 : 0)) * 12);
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
        return new ExportBudget(targetBytes, video, verdict);
    }

    private static bool SelfTestPasses()
    {
        // 20 MB Over A Minute Leaves About 2.4 Mbps
        var clip = new ExportShape(30, 1920, 1080, 60, true);
        var minute = Plan(20, clip with { Seconds = 60 });
        var silent = Plan(20, clip with { Seconds = 60, HasAudio = false });
        var totalBits = (minute.VideoBitsPerSecond + AudioBitsPerSecond) * 60;
        return minute.TargetBytes == 20_000_000
            && totalBits < 20_000_000 * 8 * (1 - SafetyMargin)
            && minute.VideoBitsPerSecond is > 2_300_000 and < 2_500_000
            && silent.VideoBitsPerSecond - minute.VideoBitsPerSecond is > 128_000 and < 136_000
            && Plan(10, clip with { Seconds = 600 }).Verdict == BudgetVerdict.TooSmall
            && Plan(50, clip with { Seconds = 10 }).Verdict == BudgetVerdict.Good
            && Plan(0.5, clip with { Seconds = 10 }).Verdict == BudgetVerdict.TooSmall
            && Plan(20, clip with { Seconds = 0 }).Verdict == BudgetVerdict.TooSmall
            && LimitOf(DiscordPlan.Off) is null
            && LimitOf(DiscordPlan.Free) == 20
            && LimitOf(DiscordPlan.NitroBasic) == 50
            && LimitOf(DiscordPlan.Nitro) == 500
            && KeepsClip(DiscordPlan.Off, 3600, 40_000_000)
            && KeepsClip(DiscordPlan.Free, 10, 1_000_000)
            && !KeepsClip(DiscordPlan.Free, 30, 1_000_000)
            && KeepsClip(DiscordPlan.NitroBasic, 30, 1_000_000)
            && !KeepsClip(DiscordPlan.NitroBasic, 60, 1_000_000)
            && KeepsClip(DiscordPlan.Nitro, 60, 1_000_000);
    }
}
