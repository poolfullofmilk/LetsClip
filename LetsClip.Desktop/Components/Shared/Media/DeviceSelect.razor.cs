using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class DeviceSelect
{
    [Parameter]
    public required IReadOnlyList<AudioDeviceInfo> Devices { get; set; }

    [Parameter]
    public required string SavedId { get; set; }

    [Parameter]
    public EventCallback<string?> ValueChanged { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    // The Microphone's Level Runs Along The Field
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // A Gone Device Records Through Automatic Until It Returns
    private string Shown =>
        Devices.Any(device => device.Id == SavedId) ? SavedId : AudioDevices.AutomaticId;

    // The Closed Field Reads Like Its Item
    private string? Detail => Devices.FirstOrDefault(device => device.Id == SavedId)?.Adapter;

    private string LabelOf(string? id)
    {
        return Devices.FirstOrDefault(device => device.Id == id)?.Name ?? "Automatic";
    }

    private bool IsChecked(string id)
    {
        // Automatic Also Checks The Device It Follows
        return id == Shown
            || (
                Shown == AudioDevices.AutomaticId
                && Devices.Any(device => device.Id == id && device.IsDefault)
            );
    }
}
