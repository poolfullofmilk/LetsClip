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
    // Pictures Land At 86 To 95%, Opus On Its Bitrate
    public long TypicalBytes => VideoBitsPerSecond > 0 ? (long)(TargetBytes * 0.92) : TargetBytes;
}
