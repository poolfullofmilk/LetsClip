using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class Notice
{
    [Parameter]
    public required string Text { get; set; }

    [Parameter]
    public Severity Severity { get; set; } = Severity.Warning;

    // Spacing From Whatever Sits Around It
    [Parameter]
    public string? Class { get; set; }

    // Warnings Read In Their Own Colour
    private string ClassName =>
        $"rounded-panel {(Severity == Severity.Warning ? "mud-warning-text" : "")} {Class}";
}
