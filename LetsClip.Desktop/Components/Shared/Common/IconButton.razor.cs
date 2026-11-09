using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class IconButton
{
    [Parameter]
    public required string Icon { get; set; }

    // The Tooltip And The Name Screen Readers Hear
    [Parameter]
    public required string Text { get; set; }

    [Parameter]
    public Placement Placement { get; set; } = Placement.Top;

    [Parameter]
    public Color Color { get; set; } = Color.Default;

    [Parameter]
    public bool Filled { get; set; }

    // A Full Button's Height And Corners, For The Player
    [Parameter]
    public bool Square { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }
}
