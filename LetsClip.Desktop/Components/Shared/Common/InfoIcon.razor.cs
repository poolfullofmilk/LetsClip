using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class InfoIcon
{
    [Parameter]
    public required string Text { get; set; }

    [Parameter]
    public bool Warning { get; set; }

    // Info Takes The Tonal Blue Like The Open Page
    private string? IconStyle => Warning ? null : "color:var(--tonal-info-text)";
}
