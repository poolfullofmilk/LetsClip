using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Tooltip
{
    // Parameters
    [Parameter]
    public string? Text { get; set; }

    [Parameter]
    public required RenderFragment ChildContent { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public string? RootClass { get; set; }
}
