using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class SaveClipButton : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private CaptureStatus Status => Controller.Recorder.Status;

    protected override void OnInitialized()
    {
        Controller.Recorder.StatusChanged += OnChanged;
        Controller.Changed += OnChanged;
        Shell.HotkeyChanged += OnChanged;
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task SaveAsync()
    {
        // The Same Save As The Hotkey, Off The Page's Thread
        var outcome = await Task.Run(Controller.SaveAsync);
        if (outcome.Error is { } error)
        {
            Snackbar.Add(error, Severity.Warning);
        }
    }

    public void Dispose()
    {
        Controller.Recorder.StatusChanged -= OnChanged;
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
