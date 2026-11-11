using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Panel
{
    [Parameter]
    public required string Title { get; set; }

    [Parameter]
    public string? Class { get; set; }

    // A Link's Target, Like Settings' Storage
    [Parameter]
    public string? Id { get; set; }

    // Buttons At The Title's Right End
    [Parameter]
    public RenderFragment? Actions { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
