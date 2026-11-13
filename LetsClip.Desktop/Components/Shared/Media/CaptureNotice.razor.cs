using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CaptureNotice : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    private CaptureStatus Status => Controller.Recorder.Status;

    // One Alert At Most, The Most Pressing First
    private (Severity Severity, string Text)? Alert =>
        Controller.ShowsError && Status.Message is { } error ? (Severity.Error, error)
        : Status is { State: CaptureState.Recording, Warning: { } warning }
            ? (Severity.Warning, warning)
        : null;

    protected override void OnInitialized()
    {
        Controller.Recorder.StatusChanged += OnChanged;
        Controller.Changed += OnChanged;
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Controller.Recorder.StatusChanged -= OnChanged;
        Controller.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
