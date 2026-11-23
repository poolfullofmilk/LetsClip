using System.Globalization;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CaptureChip : IDisposable
{
    private static readonly TimeSpan s_tick = TimeSpan.FromSeconds(1);

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required NavigationManager Navigation { get; set; }

    private bool IsError => Controller.ShowsError;

    // Recording With Nothing Wrong, The Dot Pulses
    private bool IsRecordingFine => Controller.IsRecording && !IsError;

    // Ticks Each Second Only While The Replay Fills
    private Timer? _filling;

    // How Much A Save Gives Right Now
    private string? HeldText =>
        IsRecordingFine && Controller.Recorder.HeldSeconds is >= 1 and var held
            ? $"Holds the Last {Formatter.FormatDuration(Math.Floor(held))}"
            : null;

    private string? TipText => Fix?.Text ?? HeldText;

    // What Records Now, Renewed While The Tip Stays Open
    private static readonly TimeSpan s_stillPeriod = TimeSpan.FromSeconds(2);
    private string? _still;
    private Timer? _stillTimer;
    private bool _making;

    private bool ShowsStill => Fix is null && HeldText is not null;

    // Held At The Picture's Shape Before It Loads
    private string StillStyle =>
        Controller.Recorder.FrameSize is var (width, height) && width > 0 && height > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"--still-aspect:{width / (double)height:0.####}"
            )
            : "--still-aspect:1.7778";

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

    // Only A Fix Makes The Chip Clickable And Underlined
    private EventCallback<MouseEventArgs> OpenFix =>
        Fix is { } fix ? EventCallback.Factory.Create<MouseEventArgs>(this, fix.Open) : default;

    private string? ChipClass =>
        Fix is not null ? "action-chip"
        : IsRecordingFine ? "recording"
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

    protected override void OnAfterRender(bool firstRender)
    {
        // Full, The Tooltip Stays Until The Next Start
        var filling =
            IsRecordingFine
            && Controller.Recorder.HeldSeconds < (Controller.Recorder.Options?.Seconds ?? 0);
        if (filling == _filling is not null)
        {
            return;
        }

        _filling?.Dispose();
        _filling = filling ? new Timer(_ => OnStatusChanged(), null, s_tick, s_tick) : null;
    }

    private async Task ShowTipAsync(bool open)
    {
        // Opened, A Fresh Picture, Then Another Every Two Seconds
        _stillTimer?.Dispose();
        _stillTimer = null;
        if (!open || !ShowsStill)
        {
            return;
        }

        _stillTimer = new Timer(
            _ => _ = InvokeAsync(MakeStillAsync),
            null,
            s_stillPeriod,
            s_stillPeriod
        );
        await MakeStillAsync();
    }

    private async Task MakeStillAsync()
    {
        // One At A Time, A Slow Picture Never Piles Up
        if (_making || _stillTimer is null)
        {
            return;
        }

        _making = true;
        var target = Path.Combine(AppInfo.ThumbnailsDirectory, $"still-{Guid.NewGuid():N}.jpg");
        try
        {
            Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
            if (
                await Task.Run(() => Controller.Recorder.StillAsync(target, CancellationToken.None))
            )
            {
                var shown = _still;
                _still = target;
                StateHasChanged();
                Forget(shown);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            // A Missing Picture Leaves The Tip As Text
        }
        finally
        {
            _making = false;
        }
    }

    private static void Forget(string? still)
    {
        try
        {
            if (still is not null)
            {
                File.Delete(still);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A Picture Still Shown Goes At The Next Start's Sweep
        }
    }

    public void Dispose()
    {
        Controller.Recorder.StatusChanged -= OnStatusChanged;
        Controller.Changed -= OnStatusChanged;
        _filling?.Dispose();
        _stillTimer?.Dispose();
        Forget(_still);
        GC.SuppressFinalize(this);
    }
}
