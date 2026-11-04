using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Header
{
    // Parameters
    // Without A Title Only The Content Renders
    [Parameter]
    public string? Title { get; set; }

    [Parameter]
    public string? Icon { get; set; }

    [Parameter]
    public string? Info { get; set; }

    [Parameter]
    public RenderFragment? TitleContent { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Parameter]
    public string? Class { get; set; }
}
