using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CaptureChip : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    private bool IsError => Controller.Recorder.Status.State == CaptureState.Error;

    private string Icon =>
        IsError ? Icons.Material.Rounded.Error
        : Controller.ShowsBuffering ? Icons.Material.Rounded.FiberManualRecord
        : Controller.ShowsOff ? Icons.Material.Rounded.PauseCircle
        : Icons.Material.Rounded.RadioButtonUnchecked;

    // Red Dot While Buffering, Amber When Turned Off
    private Color IconColor =>
        IsError || Controller.ShowsBuffering ? Color.Error
        : Controller.ShowsOff ? Color.Warning
        : Color.Default;

    private Color ChipColor =>
        IsError ? Color.Error
        : Controller.ShowsOff ? Color.Warning
        : Color.Default;

    protected override void OnInitialized()
    {
        Controller.Recorder.StatusChanged += OnStatusChanged;
        Controller.Changed += OnStatusChanged;
    }

    private void OnStatusChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Controller.Recorder.StatusChanged -= OnStatusChanged;
        Controller.Changed -= OnStatusChanged;
        GC.SuppressFinalize(this);
    }
}
