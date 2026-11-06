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

    // MudBlazor Defaults To Outlined Icons, LetsClip Uses Rounded
    private string Icon =>
        Severity switch
        {
            Severity.Error => Icons.Material.Rounded.Error,
            Severity.Success => Icons.Material.Rounded.CheckCircle,
            Severity.Info or Severity.Normal => Icons.Material.Rounded.Info,
            _ => Icons.Material.Rounded.Warning,
        };

    // Warnings Read In Their Own Colour
    private string ClassName =>
        $"rounded-panel {(Severity == Severity.Warning ? "mud-warning-text" : "")} {Class}";
}
