using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Pages;

public partial class Clips : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    private readonly CancellationTokenSource _closing = new();
    private IReadOnlyList<ClipInfo>? _clips;
    private int _loads;

    private string CountText =>
        _clips is { Count: > 0 } clips
            ? clips.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "";

    private string EmptyText =>
        $"No clips yet, press {Controller.Settings.Hotkey} to save the last moments";

    protected override async Task OnInitializedAsync()
    {
        Controller.Library.Changed += OnLibraryChanged;
        Controller.Recorder.ClipSaved += OnClipSaved;
        await LoadAsync();
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(LoadAsync);
    }

    private void OnClipSaved(ClipInfo clip)
    {
        _ = InvokeAsync(LoadAsync);
    }

    private async Task LoadAsync()
    {
        // A Newer Load Wins, Probing Runs Off The UI Thread
        var load = ++_loads;
        var token = _closing.Token;
        var clips = await Task.Run(() => Controller.Library.ListAsync(token), token);
        if (load == _loads && !token.IsCancellationRequested)
        {
            _clips = clips;
            StateHasChanged();
        }
    }

    private void Open(ClipInfo clip)
    {
        NavigationManager.NavigateTo(ClipRoutes.EditorOf(clip.Path));
    }

    private void OpenFolder()
    {
        Explorer.OpenFolder(Controller.Settings.ClipsDirectory);
    }

    public void Dispose()
    {
        Controller.Library.Changed -= OnLibraryChanged;
        Controller.Recorder.ClipSaved -= OnClipSaved;
        _closing.Cancel();
        _closing.Dispose();
        GC.SuppressFinalize(this);
    }
}
