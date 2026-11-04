using LetsClip.Data.Enums;

namespace LetsClip.Data.ViewModels;

public sealed record CaptureStatus(
    CaptureState State,
    string? Message,
    bool IsSaving,
    string? Warning
)
{
    public static readonly CaptureStatus Idle = new(CaptureState.Stopped, null, false, null);

    public bool IsBuffering => State == CaptureState.Buffering;

    public bool CanStart => State is CaptureState.Stopped or CaptureState.Error;
}
