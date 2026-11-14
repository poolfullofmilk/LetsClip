using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class ProblemChip
{
    [Parameter]
    public required string Text { get; set; }

    // Names The Fix A Click Opens
    [Parameter]
    public string? Tip { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    // Underlined Only While A Click Opens The Fix
    [Parameter]
    public bool Clickable { get; set; } = true;
}
