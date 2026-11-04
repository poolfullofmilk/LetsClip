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

    private CaptureStatus Status => Controller.Recorder.Status;

    private string Label =>
        Status switch
        {
            { IsSaving: true } => "Saving Clip",
            { State: CaptureState.Buffering } => Controller.BufferingText,
            { State: CaptureState.Starting } => "Starting",
            { State: CaptureState.Stopping } => "Stopping",
            { State: CaptureState.Error } => "Capture Error",
            _ => Controller.IdleText,
        };

    private string Icon =>
        Status.State switch
        {
            CaptureState.Buffering or CaptureState.Starting => Icons
                .Material
                .Rounded
                .FiberManualRecord,
            CaptureState.Error => Icons.Material.Rounded.Error,
            _ => Icons.Material.Rounded.RadioButtonUnchecked,
        };

    // The Record Dot Is Red, Errors Say So In Text
    private Color IconColor =>
        Status.State is CaptureState.Buffering or CaptureState.Starting or CaptureState.Error
            ? Color.Error
            : Color.Default;

    private Color ChipColor => Status.State == CaptureState.Error ? Color.Error : Color.Default;

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
