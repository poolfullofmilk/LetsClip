using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class SettingRow
{
    [Parameter]
    public required string Title { get; set; }

    [Parameter]
    public string? Description { get; set; }

    [Parameter]
    public RenderFragment? DescriptionContent { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    private bool HasDescription => Description is not null || DescriptionContent is not null;
}
