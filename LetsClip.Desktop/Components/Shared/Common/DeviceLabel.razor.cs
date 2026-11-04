using LetsClip.Data.ViewModels;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class DeviceLabel
{
    [Parameter]
    public required AudioDeviceInfo Device { get; set; }
}
