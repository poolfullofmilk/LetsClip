using LetsClip.Data.Enums;

namespace LetsClip.Data.ViewModels;

public sealed record CaptureStatus(
    CaptureState State,
    string? Message,
    string? MonitorLabel,
    int RequestedSeconds,
    double AvailableSeconds,
    bool IsSaving,
    int QueuedSaves,
    string? Warning
)
{
    public bool IsBuffering => State == CaptureState.Buffering;

    public bool CanStart => State is CaptureState.Stopped or CaptureState.Error;

    public bool IsWarmingUp => IsBuffering && AvailableSeconds < RequestedSeconds - 0.5;

    public static CaptureStatus Idle(int requestedSeconds)
    {
        return new CaptureStatus(
            CaptureState.Stopped,
            null,
            null,
            requestedSeconds,
            0,
            false,
            0,
            null
        );
    }
}
