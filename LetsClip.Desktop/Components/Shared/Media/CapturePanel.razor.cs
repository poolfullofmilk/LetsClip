using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CapturePanel : IDisposable
{
    // Said Once Before The First Start, Honest About What Records
    internal const string NoticeTitle = "Before Buffering Starts";
    internal const string NoticeText =
        "LetsClip records everything visible on the chosen monitor, including other apps, chat windows and notifications, into a short rolling buffer on this PC.\n\nNothing is kept until you save a clip, and nothing leaves your PC. Buffering then runs by itself whenever LetsClip runs, and the tray icon pauses it any time.\n\nStart buffering now";

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    private string? _saveError;

    private CaptureStatus Status => Controller.Recorder.Status;

    private string StateText =>
        Status switch
        {
            { IsSaving: true, QueuedSaves: > 0 } => $"Saving Clip, {Status.QueuedSaves} Waiting",
            { IsSaving: true } => "Saving Clip",
            { State: CaptureState.Buffering } => "Buffering",
            { State: CaptureState.Starting } => "Starting",
            { State: CaptureState.Stopping } => "Stopping",
            { State: CaptureState.Error } => "Error",
            _ => Controller.IdleText,
        };

    private string StateClass =>
        Status.State == CaptureState.Error ? "mud-error-text" : "mud-text-secondary";

    private string HistoryText =>
        Status.IsWarmingUp
            ? $"{Status.AvailableSeconds:0}s of {Status.RequestedSeconds}s, Warming Up"
            : $"{Status.AvailableSeconds:0}s of {Status.RequestedSeconds}s";

    private double HistoryPercent =>
        Status.RequestedSeconds > 0 ? 100 * Status.AvailableSeconds / Status.RequestedSeconds : 0;

    // Full Size Is Only Offered Above 1080p
    private string? NativeLabel =>
        Controller.SelectedMonitor is { } monitor && Math.Min(monitor.Width, monitor.Height) > 1080
            ? $"{Math.Min(monitor.Width, monitor.Height)}p"
            : null;

    private string HotkeyText =>
        Shell.HotkeyProblem ?? $"{Shell.HotkeyText} saves from anywhere, even in the tray";

    private string HotkeyClass =>
        Shell.HotkeyProblem is null ? "mud-text-secondary" : "mud-warning-text";

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

    private static string MonitorLabel(MonitorInfo? monitor)
    {
        return monitor?.FriendlyName ?? "";
    }

    private static string MonitorName(MonitorInfo monitor)
    {
        return monitor.IsPrimary ? $"{monitor.FriendlyName}, Primary" : monitor.FriendlyName;
    }

    private static string FrameRateLabel(int rate)
    {
        return $"{rate} FPS";
    }

    private static string MonitorDetails(MonitorInfo monitor)
    {
        return $"{monitor.Width} × {monitor.Height} · {monitor.RefreshRate} Hz";
    }

    private void OutlineMonitors(bool isOpen)
    {
        if (isOpen)
        {
            Shell.ShowMonitors(Controller.Monitors, Controller.SelectedMonitor);
        }
        else
        {
            Shell.HideMonitors();
        }
    }

    private void SelectMonitor(MonitorInfo monitor)
    {
        Shell.FlashMonitor(monitor);
        Controller.SelectMonitor(monitor);
    }

    private void SetReplay(int seconds)
    {
        Controller.UpdateSettings(settings => settings with { ReplaySeconds = seconds });
    }

    private void SetFullResolution(bool full)
    {
        Controller.UpdateSettings(settings => settings with { FullResolution = full });
    }

    private void SetFrameRate(int rate)
    {
        Controller.UpdateSettings(settings => settings with { FrameRate = rate });
    }

    private async Task StartAsync()
    {
        // Only Before The Notice Was Answered, Or After An Error
        _saveError = null;
        if (!Controller.Settings.HasSeenCaptureNotice)
        {
            if (!await DialogService.ConfirmAsync(NoticeTitle, NoticeText))
            {
                return;
            }

            Controller.UpdateSettings(settings => settings with { HasSeenCaptureNotice = true });
            return;
        }

        await Controller.StartAsync();
    }

    private async Task SaveAsync()
    {
        // The Button And The Hotkey Run The Same Save
        var outcome = await Controller.SaveAsync();
        _saveError = outcome.Error;
    }

    public void Dispose()
    {
        Shell.HideMonitors();
        Controller.Recorder.StatusChanged -= OnChanged;
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
