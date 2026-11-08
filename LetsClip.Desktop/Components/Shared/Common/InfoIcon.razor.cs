using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class InfoIcon
{
    [Parameter]
    public required RenderFragment ChildContent { get; set; }

    // What Screen Readers Hear Instead Of The Tooltip
    [Parameter]
    public required string Label { get; set; }
}
