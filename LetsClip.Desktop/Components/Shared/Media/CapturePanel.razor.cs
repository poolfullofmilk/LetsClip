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

    private string? _saveError;

    private CaptureStatus Status => Controller.Recorder.Status;

    // One Alert At Most, The Most Pressing First
    private (Severity Severity, string Text)? Alert =>
        Status.State == CaptureState.Error && Status.Message is { } error ? (Severity.Error, error)
        : _saveError is { } saveError ? (Severity.Warning, saveError)
        : Shell.HotkeyProblem is { } hotkey ? (Severity.Warning, hotkey)
        : Status is { State: CaptureState.Recording, Warning: { } warning }
            ? (Severity.Warning, warning)
        : Status.Message is { } message ? (Severity.Info, message)
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

    private static string MonitorLabel(MonitorInfo? monitor)
    {
        return monitor?.FriendlyName ?? "";
    }

    private static string ReplayLabel(int seconds)
    {
        return Formatter.FormatDuration(seconds);
    }

    private static string QualityLabel(CaptureQuality quality)
    {
        return quality.ToString();
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

    private void SetReplay(int seconds)
    {
        Controller.UpdateSettings(settings => settings with { ReplaySeconds = seconds });
    }

    private void SetResolution(int lines)
    {
        Controller.UpdateSettings(settings => settings with { Resolution = lines });
    }

    private void SetQuality(CaptureQuality quality)
    {
        Controller.UpdateSettings(settings => settings with { Quality = quality });
    }

    private void SetFrameRate(int rate)
    {
        Controller.UpdateSettings(settings => settings with { FrameRate = rate });
    }

    private async Task TryAgainAsync()
    {
        _saveError = null;
        await Controller.StartAutomaticallyAsync();
    }

    private async Task SaveAsync()
    {
        // The Same Save As The Hotkey, Off The Page's Thread
        var outcome = await Task.Run(Controller.SaveAsync);
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
