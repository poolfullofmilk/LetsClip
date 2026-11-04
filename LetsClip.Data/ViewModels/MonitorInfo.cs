namespace LetsClip.Data.ViewModels;

public sealed record MonitorInfo(
    string DevicePath,
    string FriendlyName,
    string GdiName,
    int Left,
    int Top,
    int Width,
    int Height,
    int Rotation,
    int RefreshRate,
    bool IsPrimary,
    bool IsHdr,
    int AdapterIndex,
    int OutputIndex,
    string AdapterName,
    int AdapterVendorId
)
{
    public string Label => $"{FriendlyName} ({Width} × {Height})";

    public string ListLabel => IsPrimary ? $"{Label}, Primary" : Label;
}
