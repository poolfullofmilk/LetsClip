using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class NoClips : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required NavigationManager Navigation { get; set; }

    // What Records Right Now, In The Background
    private string? RecordingText
    {
        get
        {
            if (Controller.ShowsError)
            {
                return null;
            }

            if (Controller.IsRecording)
            {
                return Controller.Games.GameName is { } game
                    ? $"Recording {game} in the background"
                    : $"Recording Monitor {MonitorNumber} in the background";
            }

            // Games Switched On In Games And Apps, Counted Once Scanned
            var count = Controller.GameList().Count(game => !game.IsRemoved);
            return !Controller.Settings.GamesOnly ? null
                : Controller.GamesScanned && count > 0
                    ? $"Records in the background once one of your {count} games and apps starts"
                : "Records in the background once a game or app starts";
        }
    }

    private int MonitorNumber =>
        Math.Max(1, Controller.Monitors.ToList().IndexOf(Controller.SelectedMonitor!) + 1);

    protected override void OnInitialized()
    {
        Controller.Recorder.StatusChanged += OnChanged;
        Controller.Changed += OnChanged;
        Shell.HotkeyChanged += OnChanged;
    }

    private void OpenSettings()
    {
        // The Hotkey Row Sits In Saving
        Navigation.NavigateTo("settings");
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Controller.Recorder.StatusChanged -= OnChanged;
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
