using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class RecordButton : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    // Its Own Second Ticks, Only This Button Redraws
    private Timer? _tick;

    protected override void OnInitialized()
    {
        Controller.Recorder.StatusChanged += OnChanged;
        Controller.Changed += OnChanged;
        _tick = new Timer(_ => OnTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnTick()
    {
        if (Controller.RecordingSince is not null)
        {
            OnChanged();
        }
    }

    private void Start()
    {
        if (!Controller.StartRecording())
        {
            Snackbar.Add("Nothing is being recorded right now", Severity.Warning);
        }
    }

    private async Task StopAsync()
    {
        // Saved Like A Clip, However Long It Ran
        var outcome = await Task.Run(Controller.StopRecordingAsync);
        if (outcome.Error is { } error)
        {
            Snackbar.Add(error, Severity.Warning);
        }
    }

    public void Dispose()
    {
        Controller.Recorder.StatusChanged -= OnChanged;
        Controller.Changed -= OnChanged;
        _tick?.Dispose();
        GC.SuppressFinalize(this);
    }
}
