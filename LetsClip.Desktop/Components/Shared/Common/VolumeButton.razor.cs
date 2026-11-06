using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class VolumeButton
{
    // Volume Moves In Tenths Everywhere
    public const double Step = 0.1;

    // The Button's Tooltip, Hovering It Raises The Slider
    [Parameter]
    public required string Text { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    // Zero Reads As Muted
    [Parameter]
    public double Value { get; set; }

    [Parameter]
    public EventCallback<double> ValueChanged { get; set; }

    [Parameter]
    public double Min { get; set; }

    private async Task SlideAsync(double value)
    {
        // Every Caller Gets Whole Tenths
        await ValueChanged.InvokeAsync(Math.Round(value / Step) * Step);
    }

    public static string IconOf(double volume)
    {
        return volume <= 0 ? Icons.Material.Rounded.VolumeOff
            : volume < 0.5 ? Icons.Material.Rounded.VolumeDown
            : Icons.Material.Rounded.VolumeUp;
    }
}
