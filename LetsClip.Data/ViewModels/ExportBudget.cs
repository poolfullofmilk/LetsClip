using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;

namespace LetsClip.Data.ViewModels;

public sealed record ExportBudget(
    long TargetBytes,
    double VideoBitsPerSecond,
    BudgetVerdict Verdict,
    int Lines = SizeBudget.FullLines,
    double FrameRate = 60,
    double AudioBitsPerSecond = SizeBudget.AudioBitsPerSecond
)
{
    // Where Encodes Land, Measured 86 To 95% Of The Limit
    public long TypicalBytes => (long)(TargetBytes * 0.92);
}
