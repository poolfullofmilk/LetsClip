namespace LetsClip.Data.ViewModels;

public sealed record MonitorInfo(
    string DevicePath,
    string FriendlyName,
    int Left,
    int Top,
    int Width,
    int Height,
    int Rotation,
    int RefreshRate,
    bool IsPrimary,
    bool IsHdr,
    int AdapterIndex,
    int OutputIndex
)
{
    public string Label => $"{FriendlyName}: {Width} × {Height}";
}
