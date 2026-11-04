using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Pages;

public partial class Clips : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    internal const string DeleteTitle = "Delete Clip";
    internal const string DeleteText = "It goes to the Recycle Bin, its exports stay";

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
        await LoadAsync();
    }

    private void OnLibraryChanged()
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

    private async Task DeleteAsync(ClipInfo clip)
    {
        if (!await DialogService.ConfirmAsync(DeleteTitle, DeleteText))
        {
            return;
        }

        if (!Explorer.Recycle(clip.Path))
        {
            Snackbar.Add("The clip could not be deleted, it may be in use", Severity.Warning);
        }
    }

    private void OpenFolder()
    {
        Explorer.OpenFolder(Controller.Settings.ClipsDirectory);
    }

    public void Dispose()
    {
        Controller.Library.Changed -= OnLibraryChanged;
        _closing.Cancel();
        _closing.Dispose();
        GC.SuppressFinalize(this);
    }
}
