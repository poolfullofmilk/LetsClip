namespace LetsClip.Data.Enums;

[Flags]
public enum CaptureHolds
{
    None = 0,
    Locked = 1,
    Asleep = 2,
    DisplayOff = 4,
}
