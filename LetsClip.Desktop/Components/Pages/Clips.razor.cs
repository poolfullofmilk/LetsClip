using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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

    [Inject]
    public required IJSRuntime JavaScript { get; set; }

    private readonly CancellationTokenSource _closing = new();
    private DotNetObjectReference<Clips>? _reference;
    private IReadOnlyList<ClipInfo>? _clips;
    private IReadOnlyList<ClipInfo>? _nextClips;
    private int _loads;

    // The Games Shown, Null For All, Kept While LetsClip Runs
    private static HashSet<string>? s_shownGames;

    private HashSet<string> _draft = [];
    private string? _search;
    private bool _filterOpen;

    private string CountText =>
        _clips is not { Count: > 0 } clips ? ""
        : IsFiltered ? $"{ShownClips.Count} of {clips.Count}"
        : $"{clips.Count}";

    private static bool IsFiltered => s_shownGames is not null;

    private IReadOnlyList<ClipInfo> ShownClips =>
        s_shownGames is { } shown
            ? [.. (_clips ?? []).Where(clip => shown.Contains(GameOf(clip)))]
            : _clips ?? [];

    private IReadOnlyList<string> Games =>
        [
            .. (_clips ?? [])
                .Select(GameOf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase),
        ];

    private IEnumerable<string> SearchedGames =>
        Games.Where(game =>
            string.IsNullOrWhiteSpace(_search)
            || game.Contains(_search.Trim(), StringComparison.CurrentCultureIgnoreCase)
        );

    // Checked, Unchecked Or Some
    private bool? AllState =>
        Games.All(_draft.Contains) ? true
        : _draft.Count == 0 ? false
        : null;

    protected override async Task OnInitializedAsync()
    {
        // Saves List At Once, The Watcher Covers Explorer
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
        // A Newer Load Wins, A Closed Page Loads Nothing
        if (_closing.IsCancellationRequested)
        {
            return;
        }

        var load = ++_loads;
        var token = _closing.Token;
        try
        {
            var clips = await Task.Run(() => Controller.Library.ListAsync(token), token);
            if (load == _loads && !token.IsCancellationRequested)
            {
                await ShowAsync(clips);
            }
        }
        catch (OperationCanceledException)
        {
            // Left The Page Mid Load
        }
    }

    private async Task ShowAsync(IReadOnlyList<ClipInfo> clips)
    {
        // Cards Slide To New Places, Others Fade In Or Out
        if (
            _clips is not null
            && _clips.Select(clip => clip.Path).SequenceEqual(clips.Select(clip => clip.Path))
        )
        {
            _clips = clips;
            StateHasChanged();
            return;
        }

        _nextClips = clips;
        _reference ??= DotNetObjectReference.Create(this);
        try
        {
            await JavaScript.InvokeVoidAsync(
                "letsClip.transition",
                _reference,
                nameof(ShowNextClips)
            );
        }
        catch (JSDisconnectedException)
        {
            // The Window Is Closing, Nothing Left To Show
        }
    }

    [JSInvokable]
    public void ShowNextClips()
    {
        _clips = _nextClips ?? _clips;
        StateHasChanged();
    }

    private static string CardName(ClipInfo clip)
    {
        // Its Own Name, So The Card Moves Rather Than Fades
        return $"clip-{(uint)clip.Path.GetHashCode(StringComparison.OrdinalIgnoreCase)}";
    }

    private static IEnumerable<string> FactsOf(ClipInfo clip)
    {
        // Stacked From The Bottom Left, The Date Last
        if (clip.Width > 0)
        {
            yield return $"{clip.Width} × {clip.Height}";
        }

        yield return Formatter.FormatBitrate(clip.SizeBytes * 8 / clip.Duration);
        yield return Formatter.FormatSize(clip.SizeBytes);
        yield return Formatter.FormatDateTime(clip.RecordedAt);
    }

    #region Game Filter
    private static string GameOf(ClipInfo clip)
    {
        return clip.Game ?? ClipInfo.MonitorTitle;
    }

    private void OpenFilter()
    {
        // The Popover Edits A Copy Until Apply
        _draft = new HashSet<string>(s_shownGames ?? [.. Games], StringComparer.OrdinalIgnoreCase);
        _search = null;
        _filterOpen = true;
    }

    private void CloseFilter()
    {
        _filterOpen = false;
    }

    private void SetGame(string game, bool shown)
    {
        _ = shown ? _draft.Add(game) : _draft.Remove(game);
    }

    private void SetAll(bool? shown)
    {
        _draft = new HashSet<string>(shown == true ? Games : [], StringComparer.OrdinalIgnoreCase);
    }

    private void ResetFilter()
    {
        SetAll(true);
        _search = null;
    }

    private void ApplyFilter()
    {
        // Every Game Checked Means No Filter, New Games Show Too
        s_shownGames = Games.All(_draft.Contains)
            ? null
            : new HashSet<string>(_draft, StringComparer.OrdinalIgnoreCase);
        _filterOpen = false;
    }

    private static void ShowAll()
    {
        s_shownGames = null;
    }
    #endregion

    private void Open(ClipInfo clip)
    {
        NavigationManager.NavigateTo(ClipRoutes.EditorOf(clip.Path));
    }

    private async Task DeleteAsync(ClipInfo clip)
    {
        if (!await DialogService.ConfirmDeleteAsync())
        {
            return;
        }

        if (!Explorer.Recycle(clip.Path))
        {
            Snackbar.Add("The clip could not be deleted, it may be in use", Severity.Warning);
            return;
        }

        // The Card Goes At Once, The Watcher's Reload Then Agrees
        ++_loads;
        await ShowAsync([.. (_clips ?? []).Where(shown => shown.Path != clip.Path)]);
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
        _reference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
