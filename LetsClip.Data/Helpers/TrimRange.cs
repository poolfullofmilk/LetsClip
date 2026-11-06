using System.Diagnostics;

namespace LetsClip.Data.Helpers;

public readonly record struct TrimRange(double Start, double End)
{
    // One Keyboard Step, Also The Shortest Selection
    public const double Step = 0.1;

    // Points Sit On Frame Starts, FFmpeg Seeks This Much Earlier
    public const double EdgeMargin = 0.001;
    private const double Tolerance = 0.0005;

    static TrimRange()
    {
        Debug.Assert(SelfTestPasses());
    }

    public double Length => End - Start;

    public bool IsValidFor(double duration)
    {
        // Start Before End Inside The Clip, At Least One Step
        return double.IsFinite(Start)
            && double.IsFinite(End)
            && duration > 0
            && Start >= 0
            && End <= duration + Tolerance
            && End - Start >= Step - Tolerance;
    }

    public static TrimRange Whole(double duration)
    {
        return new TrimRange(0, Math.Max(0, duration));
    }

    public static TrimRange Fit(double start, double end, double duration)
    {
        // Ranges From Metadata Land Inside The Real File
        var length = Math.Max(0, duration);
        var range = new TrimRange(Math.Clamp(start, 0, length), Math.Clamp(end, 0, length));
        return range.IsValidFor(length) ? range : Whole(length);
    }

    private static bool SelfTestPasses()
    {
        var range = new TrimRange(2, 10);
        return range.IsValidFor(10)
            && !range.IsValidFor(9.9)
            && !new TrimRange(5, 5).IsValidFor(10)
            && !new TrimRange(-0.1, 5).IsValidFor(10)
            && !new TrimRange(double.NaN, 5).IsValidFor(10)
            && Fit(4, 3, 10) == Whole(10)
            && Fit(1, 30, 10) == new TrimRange(1, 10);
    }
}
