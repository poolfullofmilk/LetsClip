using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CaptureChip : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required NavigationManager Navigation { get; set; }

    private bool IsError => Controller.ShowsError;

    private string Icon =>
        IsError ? Icons.Material.Rounded.Error
        : Controller.IsRecording ? Icons.Material.Rounded.FiberManualRecord
        : Icons.Material.Rounded.RadioButtonUnchecked;

    // The Record Dot Is Red, Errors Say So In Text
    private Color IconColor => IsError || Controller.IsRecording ? Color.Error : Color.Default;

    private Color ChipColor => IsError ? Color.Error : Color.Default;

    // Waiting Or Blocked, A Click Opens What Fixes It
    private (string Text, Func<Task> Open)? Fix =>
        IsError
            ? Controller.Recorder.Status.Fix switch
            {
                CaptureFix.Display => ("Open Display Settings", () => Open("ms-settings:display")),
                CaptureFix.Storage => (
                    "Open Storage Settings",
                    () => Open("ms-settings:storagesense")
                ),
                CaptureFix.Monitor => ("Pick Another Monitor", OpenSettings),
                CaptureFix.Reinstall => (
                    "Download LetsClip Again",
                    () => Open(AppInfo.LatestReleaseUrl)
                ),
                _ => null,
            }
        : Controller.Settings.GamesOnly
        && !Controller.IsRecording
        && !Controller.Recorder.Status.IsSaving
            ? ("Edit Games and Apps", () => GamesDialog.ShowAsync(DialogService))
        : null;

    private static Task Open(string target)
    {
        Explorer.Open(target);
        return Task.CompletedTask;
    }

    private Task OpenSettings()
    {
        // The Monitor Pick Sits At The Top Of Recording
        Navigation.NavigateTo("settings");
        return Task.CompletedTask;
    }

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
