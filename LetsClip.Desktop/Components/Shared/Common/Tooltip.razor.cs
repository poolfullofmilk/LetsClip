using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Tooltip
{
    [Parameter]
    public string? Text { get; set; }

    [Parameter]
    public required RenderFragment ChildContent { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    // Most Tooltips Wrap One Button In A Row
    [Parameter]
    public string RootClass { get; set; } = "d-flex";
}
