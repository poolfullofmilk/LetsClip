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
    public bool Filled { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    // Zero Reads As Muted
    [Parameter]
    public double Value { get; set; }

    [Parameter]
    public EventCallback<double> ValueChanged { get; set; }

    // Microphone Or Voice Chat Show Themselves, Never A Speaker
    [Parameter]
    public string? OnIcon { get; set; }

    [Parameter]
    public string? OffIcon { get; set; }

    // Without It The Button Only Mutes, No Slider Rises
    [Parameter]
    public bool Slides { get; set; } = true;

    private string Icon =>
        OnIcon is null || OffIcon is null ? IconOf(Value)
        : Value <= 0 ? OffIcon
        : OnIcon;

    private async Task SlideAsync(double value)
    {
        // Every Caller Gets Whole Tenths
        await ValueChanged.InvokeAsync(Math.Round(value, 1));
    }

    public static string IconOf(double volume)
    {
        return volume <= 0 ? Icons.Material.Rounded.VolumeOff
            : volume < 0.5 ? Icons.Material.Rounded.VolumeDown
            : Icons.Material.Rounded.VolumeUp;
    }
}
