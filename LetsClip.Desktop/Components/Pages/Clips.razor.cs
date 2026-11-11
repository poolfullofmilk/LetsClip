using LetsClip.Data.Enums;
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

    public const string Route = "clips";

    // Cards Render In Batches, More As The Last One Nears
    private const int BatchSize = 60;

    // Past This Many Cards A Change Only Fades
    private const int NamedCards = 2 * BatchSize;

    private readonly CancellationTokenSource _closing = new();
    private DotNetObjectReference<Clips>? _reference;
    private Action? _nextChange;
    private int _loads;
    private int _cards = BatchSize;

    // The Last List, So Coming Back Never Shows A Spinner
    private static IReadOnlyList<ClipInfo>? s_clips;

    // The Games Shown, Null For All, Kept While LetsClip Runs
    private static HashSet<string>? s_shownGames;

    private HashSet<string> _draft = [];
    private string? _search;
    private bool _filterOpen;

    private DotNetObjectReference<Clips> Reference =>
        _reference ??= DotNetObjectReference.Create(this);

    private static bool IsFiltered => s_shownGames is not null;

    // What The Cards Show, Filtered Then Ordered
    private IReadOnlyList<ClipInfo> ShownClips
    {
        get
        {
            var shown = (s_clips ?? []).Where(clip => s_shownGames?.Contains(clip.Source) != false);
            var ordered = Controller.Settings.ClipSort switch
            {
                ClipSort.Oldest => shown.OrderBy(clip => clip.RecordedAt),
                ClipSort.Largest => shown.OrderByDescending(clip => clip.SizeBytes),
                ClipSort.Longest => shown.OrderByDescending(clip => clip.Duration),
                _ => shown.OrderByDescending(clip => clip.RecordedAt),
            };
            return [.. ordered];
        }
    }

    private static IReadOnlyList<string> Games =>
        [
            .. (s_clips ?? [])
                .Select(clip => clip.Source)
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
        // The Same Cards Just Update
        if (
            s_clips is not null
            && s_clips.Select(clip => clip.Path).SequenceEqual(clips.Select(clip => clip.Path))
        )
        {
            s_clips = clips;
            StateHasChanged();
            return;
        }

        await ChangeAsync(() => s_clips = clips);
    }

    private async Task ChangeAsync(Action change, bool fromTop = false)
    {
        // Cards Slide To New Places, Others Fade In Or Out
        _nextChange += change;
        if (fromTop)
        {
            // A New Order Or Filter Starts At Its First Batch
            _nextChange += () => _cards = BatchSize;
        }

        await JavaScript.CallAsync(
            "letsClip.transition",
            Reference,
            nameof(ShowNextChange),
            fromTop
        );
    }

    [JSInvokable]
    public void ShowNextChange()
    {
        Interlocked.Exchange(ref _nextChange, null)?.Invoke();
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Ponytail: Each Batch Rebuilds Every Shown Card, Fine To Thousands
        if (ShownClips.Count > _cards)
        {
            await JavaScript.CallAsync("letsClip.watchEnd", Reference, nameof(ShowMoreCards));
        }
    }

    [JSInvokable]
    public void ShowMoreCards()
    {
        if (ShownClips.Count > _cards)
        {
            _cards += BatchSize;
            StateHasChanged();
        }
    }

    private async Task SetSortAsync(ClipSort sort)
    {
        await ChangeAsync(
            () => Controller.UpdateSettings(settings => settings with { ClipSort = sort }),
            fromTop: true
        );
    }

    private static string CountText(int shown)
    {
        return s_clips is not { Count: > 0 } clips ? ""
            : IsFiltered ? $"{shown} of {clips.Count}"
            : $"{clips.Count}";
    }

    private string? CardStyle(ClipInfo clip)
    {
        // Its Own Name, So The Card Moves Rather Than Fades
        return _cards > NamedCards
            ? null
            : $"view-transition-name:clip-{(uint)clip.Path.GetHashCode(StringComparison.OrdinalIgnoreCase)}";
    }

    private static IEnumerable<(string Label, string Value)> FactsOf(ClipInfo clip)
    {
        // Old Metadata May Not Know The Resolution Yet
        if (clip.Width > 0)
        {
            yield return ("Resolution", $"{clip.Width} × {clip.Height}");
        }

        yield return ("Bitrate", Formatter.FormatBitrate(clip.SizeBytes * 8 / clip.Duration));
        yield return ("Size", Formatter.FormatSize(clip.SizeBytes));
    }

    private static string FactsText(ClipInfo clip)
    {
        return string.Join(", ", FactsOf(clip).Select(fact => $"{fact.Label} {fact.Value}"));
    }

    #region Game Filter
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

    private async Task ApplyFilterAsync()
    {
        // Every Game Checked Means No Filter, New Games Show Too
        var shown = Games.All(_draft.Contains)
            ? null
            : new HashSet<string>(_draft, StringComparer.OrdinalIgnoreCase);
        _filterOpen = false;
        await ChangeAsync(() => s_shownGames = shown, fromTop: true);
    }

    private async Task ShowAllAsync()
    {
        await ChangeAsync(() => s_shownGames = null, fromTop: true);
    }
    #endregion

    public static string EditorOf(string path)
    {
        return $"{Route}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }

    private void Open(ClipInfo clip)
    {
        NavigationManager.NavigateTo(EditorOf(clip.Path));
    }

    private async Task DeleteAsync(ClipInfo clip)
    {
        if (!await YesNoDialog.ConfirmDeleteAsync(DialogService))
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
        await ShowAsync([.. (s_clips ?? []).Where(shown => shown.Path != clip.Path)]);
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
