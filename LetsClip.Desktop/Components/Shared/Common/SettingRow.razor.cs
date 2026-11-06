using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class SettingRow
{
    [Parameter]
    public required string Title { get; set; }

    [Parameter]
    public string? Info { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // A Switch At The Row's End, Named By Its Title
    [Parameter]
    public bool? Switch { get; set; }

    [Parameter]
    public EventCallback<bool> SwitchChanged { get; set; }

    [Parameter]
    public bool SwitchDisabled { get; set; }
}
