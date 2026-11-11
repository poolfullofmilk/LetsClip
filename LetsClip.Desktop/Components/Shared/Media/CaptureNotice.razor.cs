using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CaptureNotice : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    private CaptureStatus Status => Controller.Recorder.Status;

    // One Alert At Most, The Most Pressing First
    private (Severity Severity, string Text)? Alert =>
        Controller.ShowsError && Status.Message is { } error ? (Severity.Error, error)
        : Shell.HotkeyProblem is { } hotkey ? (Severity.Warning, hotkey)
        : Status is { State: CaptureState.Recording, Warning: { } warning }
            ? (Severity.Warning, warning)
        : null;

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

    public void Dispose()
    {
        Controller.Recorder.StatusChanged -= OnChanged;
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
