using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class PlayerButton
{
    [Parameter]
    public required string Icon { get; set; }

    [Parameter]
    public required string Text { get; set; }

    [Parameter]
    public bool Filled { get; set; }

    [Parameter]
    public string? Class { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }
}
