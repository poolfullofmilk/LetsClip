using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class MediaChips
{
    [Parameter]
    public int Width { get; set; }

    [Parameter]
    public int Height { get; set; }

    [Parameter]
    public double FrameRate { get; set; }

    // Filled Over A Picture, Plain In A Row
    [Parameter]
    public bool OnImage { get; set; }

    [Parameter]
    public string? Class { get; set; }

    private Variant Variant => OnImage ? Variant.Filled : Variant.Text;

    private Color Color => OnImage ? Color.Dark : Color.Default;
}
