using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class DeviceLabel
{
    [Parameter]
    public required string Text { get; set; }

    // The Device Automatic Plays Through Right Now
    [Parameter]
    public string? Detail { get; set; }
}
