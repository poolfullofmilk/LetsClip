using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class TrimValue
{
    [Parameter]
    public required string Label { get; set; }

    [Parameter]
    public required string Value { get; set; }
}
