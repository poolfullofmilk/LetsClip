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
        "LetsClip records everything visible on the chosen monitor, including other apps, chat windows and notifications, into a short rolling buffer on this PC. Games Only records just the game's window.\n\nNothing is kept until you save a clip, and nothing leaves your PC. Buffering then runs by itself whenever LetsClip runs, and the tray icon pauses it any time.\n\nStart buffering now";

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    private string? _saveError;

    private CaptureStatus Status => Controller.Recorder.Status;

    // One Alert At Most, The Most Pressing First
    private (Severity Severity, string Text)? Alert =>
        Status.State == CaptureState.Error && Status.Message is { } error ? (Severity.Error, error)
        : _saveError is { } saveError ? (Severity.Warning, saveError)
        : Shell.HotkeyProblem is { } hotkey ? (Severity.Warning, hotkey)
        : Status is { IsBuffering: true, Warning: { } warning } ? (Severity.Warning, warning)
        : Status.Message is { } message ? (Severity.Info, message)
        : null;

    // Full Size Is Only Offered Above 1080p
    private string? NativeLabel =>
        Controller.SelectedMonitor is { } monitor && Math.Min(monitor.Width, monitor.Height) > 1080
            ? $"{Math.Min(monitor.Width, monitor.Height)}p"
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

    private static string AlertClass(Severity severity)
    {
        return severity == Severity.Warning ? "rounded-panel mud-warning-text" : "rounded-panel";
    }

    private static string MonitorLabel(MonitorInfo? monitor)
    {
        return monitor?.FriendlyName ?? "";
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

    private void SetQuality(CaptureQuality quality)
    {
        Controller.UpdateSettings(settings => settings with { Quality = quality });
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
            if (await DialogService.ConfirmAsync(NoticeTitle, NoticeText))
            {
                Controller.UpdateSettings(settings =>
                    settings with
                    {
                        HasSeenCaptureNotice = true,
                    }
                );
            }

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
