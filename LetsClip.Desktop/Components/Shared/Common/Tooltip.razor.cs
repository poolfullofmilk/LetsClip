using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Tooltip
{
    [Parameter]
    public string? Text { get; set; }

    // Richer Than Text, Like The Clip Facts
    [Parameter]
    public RenderFragment? Content { get; set; }

    [Parameter]
    public required RenderFragment ChildContent { get; set; }

    [Parameter]
    public Placement Placement { get; set; } = Placement.Top;

    [Parameter]
    public bool Disabled { get; set; }

    // Off For Buttons, Clicks Would Leave It Showing
    [Parameter]
    public bool ShowOnFocus { get; set; }

    // Most Tooltips Wrap One Button In A Row
    [Parameter]
    public string RootClass { get; set; } = "d-flex";
}
