using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CaptureChip : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    private bool IsError => Controller.ShowsError;

    private string Icon =>
        IsError ? Icons.Material.Rounded.Error
        : Controller.IsRecording ? Icons.Material.Rounded.FiberManualRecord
        : Icons.Material.Rounded.RadioButtonUnchecked;

    // The Record Dot Is Red, Errors Say So In Text
    private Color IconColor => IsError || Controller.IsRecording ? Color.Error : Color.Default;

    private Color ChipColor => IsError ? Color.Error : Color.Default;

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
