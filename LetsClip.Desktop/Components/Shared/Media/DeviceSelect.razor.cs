using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class DeviceSelect
{
    // Null While Windows Is Asked, Empty When It Has None
    [Parameter]
    public required IReadOnlyList<AudioDeviceInfo>? Devices { get; set; }

    [Parameter]
    public required string SavedId { get; set; }

    [Parameter]
    public EventCallback<string?> ValueChanged { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    // Shown In The Field When Windows Has No Such Device
    [Parameter]
    public required string Missing { get; set; }

    // The Microphone's Level Runs Along The Field
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    private IReadOnlyList<AudioDeviceInfo> Listed => Devices ?? [];

    // A Gone Device Records Through Automatic Until It Returns
    private string Shown =>
        Listed.Any(device => device.Id == SavedId) ? SavedId : AudioDevices.AutomaticId;

    // The Closed Field Reads Like Its Item
    private string? Detail => Listed.FirstOrDefault(device => device.Id == SavedId)?.Adapter;

    private string LabelOf(string? id)
    {
        return Devices is []
            ? Missing
            : Listed.FirstOrDefault(device => device.Id == id)?.Name ?? "Automatic";
    }

    private bool IsChecked(string id)
    {
        // Automatic Also Checks The Device It Follows
        return id == Shown
            || (
                Shown == AudioDevices.AutomaticId
                && Listed.Any(device => device.Id == id && device.IsDefault)
            );
    }
}
