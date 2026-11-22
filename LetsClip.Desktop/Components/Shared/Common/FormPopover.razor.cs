using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class FormPopover
{
    [Parameter]
    public bool Open { get; set; }

    // An Outside Click Closes It
    [Parameter]
    public EventCallback Closed { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // Reset Or A Quiet Action Left, The Primary Right
    [Parameter]
    public RenderFragment? Footer { get; set; }
}
