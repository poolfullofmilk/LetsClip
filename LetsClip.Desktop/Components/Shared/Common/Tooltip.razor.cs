using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Tooltip
{
    [Parameter]
    public string? Text { get; set; }

    [Parameter]
    public required RenderFragment ChildContent { get; set; }

    [Parameter]
    public Placement Placement { get; set; } = Placement.Top;

    [Parameter]
    public bool Disabled { get; set; }

    // Most Tooltips Wrap One Button In A Row
    [Parameter]
    public string RootClass { get; set; } = "d-flex";
}
