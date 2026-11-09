using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Header
{
    [Parameter]
    public required string Title { get; set; }

    [Parameter]
    public RenderFragment? TitleContent { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // A Page Filling The Window Scrolls Below Its Header
    [Parameter]
    public bool Scrolls { get; set; }
}
