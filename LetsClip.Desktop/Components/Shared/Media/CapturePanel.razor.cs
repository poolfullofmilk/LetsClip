using LetsClip.Data.Enums;
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
        : Status is { IsBuffering: true, Warning: { } warning } ? (Severity.Warning, warning)
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

    private static string FrameRateLabel(int rate)
    {
        return $"{rate} FPS";
    }

    private static string ResolutionLabel(int lines)
    {
        return $"{lines}p";
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

    private void StartBuffering()
    {
        // The Same Saved Switch As Settings And The Tray
        _saveError = null;
        Controller.SetBuffering(true);
    }

    private async Task TryAgainAsync()
    {
        _saveError = null;
        await Controller.StartAutomaticallyAsync();
    }

    private async Task SaveAsync()
    {
        // The Same Save As The Hotkey, Off The Page's Thread
        var outcome = await Task.Run(Controller.Recorder.SaveAsync);
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
