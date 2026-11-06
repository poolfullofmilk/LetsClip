using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class SettingField
{
    [Parameter]
    public required string Title { get; set; }

    [Parameter]
    public string? Info { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
