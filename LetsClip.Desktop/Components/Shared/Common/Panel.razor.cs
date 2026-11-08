using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Panel
{
    [Parameter]
    public required string Title { get; set; }

    [Parameter]
    public string? Class { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
