using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class CapturePanel : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private string? _saveError;

    private CaptureStatus Status => Controller.Recorder.Status;

    private bool IsRecommended =>
        Controller.Resolution == Controller.RecommendedResolution
        && Controller.FrameRate == Controller.RecommendedFrameRate
        && Controller.Settings.Quality == CaptureController.RecommendedQuality;

    // One Alert At Most, The Most Pressing First
    private (Severity Severity, string Text)? Alert =>
        Controller.ShowsError && Status.Message is { } error ? (Severity.Error, error)
        : _saveError is { } saveError ? (Severity.Warning, saveError)
        : Shell.HotkeyProblem is { } hotkey ? (Severity.Warning, hotkey)
        : Status is { State: CaptureState.Recording, Warning: { } warning }
            ? (Severity.Warning, warning)
        : Status is { State: not CaptureState.Error, Message: { } message }
            ? (Severity.Info, message)
        : null;

    protected override void OnInitialized()
    {
        Controller.Recorder.StatusChanged += OnChanged;
        Controller.Recorder.ClipSaved += OnClipSaved;
        Controller.Changed += OnChanged;
        Shell.HotkeyChanged += OnChanged;
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnClipSaved(ClipInfo clip)
    {
        // Any Later Save Makes An Old Failure Stale
        _saveError = null;
        OnChanged();
    }

    private static string MonitorLabel(MonitorInfo? monitor)
    {
        return monitor?.FriendlyName ?? "";
    }

    private static string? MonitorDetail(MonitorInfo? monitor)
    {
        // The Closed Field Shows Its Item's Detail On One Line
        return monitor is null
            ? null
            : $"{monitor.Width} × {monitor.Height} · {monitor.RefreshRate} Hz";
    }

    private static string? RecommendedText(bool isRecommended)
    {
        return isRecommended ? "Recommended" : null;
    }

    private static string ReplayLabel(int seconds)
    {
        return Formatter.FormatDuration(seconds);
    }

    private static string FrameRateLabel(int rate)
    {
        return $"{rate} FPS";
    }

    private static string ResolutionLabel(int lines)
    {
        return $"{lines}p";
    }

    private static Color ResolutionColor(int lines)
    {
        // Heavier Picks Read Amber, The Heaviest Red
        return lines > 1440 ? Color.Error
            : lines > 1080 ? Color.Warning
            : Color.Default;
    }

    private static Color FrameRateColor(int rate)
    {
        return rate >= 165 ? Color.Error
            : rate >= 120 ? Color.Warning
            : Color.Default;
    }

    private static Color QualityColor(CaptureQuality quality)
    {
        return quality switch
        {
            CaptureQuality.Max => Color.Error,
            CaptureQuality.High => Color.Warning,
            _ => Color.Default,
        };
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
        Controller.UpdateSettings(settings =>
            settings with
            {
                MonitorDevicePath = monitor.DevicePath,
            }
        );
    }

    private void SetGamesOnly(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { GamesOnly = enabled });
    }

    private void SetReplay(int seconds)
    {
        Controller.UpdateSettings(settings => settings with { ReplaySeconds = seconds });
    }

    private void SetResolution(int lines)
    {
        var recommended = Controller.RecommendedResolution;
        WarnOnRaise(
            lines > Controller.Resolution && lines > recommended,
            $"Above {ResolutionLabel(recommended)} looks a little sharper, costs more CPU and disk, and a Discord plan exports at 1080p anyway"
        );
        Controller.UpdateSettings(settings => settings with { Resolution = lines });
    }

    private void SetQuality(CaptureQuality quality)
    {
        // VMAF Measured Under One Noticeable Step For Much More Disk
        WarnOnRaise(
            quality > Controller.Settings.Quality && quality > CaptureController.RecommendedQuality,
            quality == CaptureQuality.Max
                ? "Max looks almost the same as Normal and takes up to 70% more disk"
                : "High looks almost the same as Normal and takes about 30% more disk",
            everywhere: true
        );
        Controller.UpdateSettings(settings => settings with { Quality = quality });
    }

    private void SetFrameRate(int rate)
    {
        var recommended = Controller.RecommendedFrameRate;
        WarnOnRaise(
            rate > Controller.FrameRate && rate > recommended,
            $"Above {FrameRateLabel(recommended)} costs two to four times the CPU, and a Discord plan exports at 60 FPS anyway"
        );
        Controller.UpdateSettings(settings => settings with { FrameRate = rate });
    }

    private void WarnOnRaise(bool raised, string cost, bool everywhere = false)
    {
        // Only Raising Past The Recommended Pick, Never Stepping Down
        if (!raised)
        {
            return;
        }

        Snackbar.Add(
            everywhere || Controller.HasHardwareEncoder
                ? cost
                : "Without a graphics card encoder this records on the CPU and can slow games down",
            Severity.Warning
        );
    }

    private void UseRecommended()
    {
        // One Change, So Capture Restarts Once
        var (resolution, rate) = (
            Controller.RecommendedResolution,
            Controller.RecommendedFrameRate
        );
        Controller.UpdateSettings(settings =>
            settings with
            {
                Resolution = resolution,
                FrameRate = rate,
                Quality = CaptureController.RecommendedQuality,
            }
        );
    }

    private async Task TryAgainAsync()
    {
        _saveError = null;
        await Controller.StartAutomaticallyAsync();
    }

    private async Task SaveAsync()
    {
        // A Press While Saving Does Nothing, The Spinner Shows Why
        if (Status.IsSaving)
        {
            return;
        }

        // The Same Save As The Hotkey, Off The Page's Thread
        var outcome = await Task.Run(Controller.SaveAsync);
        _saveError = outcome.Error;
    }

    public void Dispose()
    {
        Shell.HideMonitors();
        Controller.Recorder.StatusChanged -= OnChanged;
        Controller.Recorder.ClipSaved -= OnClipSaved;
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
