using LetsClip.Desktop.Interop;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class HotkeyChip : IDisposable
{
    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required NavigationManager Navigation { get; set; }

    protected override void OnInitialized()
    {
        Shell.HotkeyChanged += OnChanged;
    }

    private void OpenSettings()
    {
        // The Hotkey Row Sits There, With The Reason Under It
        Navigation.NavigateTo("settings");
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Shell.HotkeyChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
