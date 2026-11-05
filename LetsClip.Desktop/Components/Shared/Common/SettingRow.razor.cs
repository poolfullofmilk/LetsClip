using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class SettingRow
{
    [Parameter]
    public required string Title { get; set; }

    // Explained In A Tooltip Beside The Title
    [Parameter]
    public string? Info { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }
}
