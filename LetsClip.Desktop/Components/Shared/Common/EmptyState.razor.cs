using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class EmptyState
{
    // Parameters
    [Parameter]
    public required string Icon { get; set; }

    [Parameter]
    public Color Color { get; set; } = Color.Info;

    [Parameter]
    public required string Text { get; set; }

    // Actions Or Prompts Below The Text
    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
