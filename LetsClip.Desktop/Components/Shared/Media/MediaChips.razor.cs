using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class MediaChips
{
    [Parameter]
    public int Width { get; set; }

    [Parameter]
    public int Height { get; set; }

    [Parameter]
    public double FrameRate { get; set; }

    [Parameter]
    public string? Class { get; set; }
}
