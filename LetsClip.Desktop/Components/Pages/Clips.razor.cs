using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
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

    // The Last List, So Coming Back Never Shows A Spinner
    private static IReadOnlyList<ClipInfo>? s_clips;

    // Cards Drawn Before, So Coming Back Lands In Place
    private static int s_cards = BatchSize;

    // The Games Shown, Null For All, Kept While LetsClip Runs
    private static HashSet<string>? s_shownGames;

    // Only Unedited Clips, Kept Like The Games
    private static bool s_uneditedOnly;

    // Names Or Games Containing This, Kept Like The Filter
    private static string? s_query;

    // A Game Picked Elsewhere Starts At The Top
    private static bool s_fromTop;

    private HashSet<string> _draft = [];
    private bool _draftUnedited;
    private string? _search;
    private bool _filterOpen;

    private DotNetObjectReference<Clips> Reference =>
        _reference ??= DotNetObjectReference.Create(this);

    private static bool IsFiltered => s_shownGames is not null || s_uneditedOnly;

    private static bool IsSearched => !string.IsNullOrWhiteSpace(s_query);

    // Sorted By Time, Each Day Gets Its Own Header
    private bool IsByDay => Controller.Settings.ClipSort is ClipSort.Newest or ClipSort.Oldest;

    // What The Cards Show, Filtered Then Ordered
    private IReadOnlyList<ClipInfo> ShownClips
    {
        get
        {
            var shown = (s_clips ?? []).Where(clip =>
                s_shownGames?.Contains(clip.Source) != false
                && !(s_uneditedOnly && clip.IsEdited)
                && Matches(clip)
            );
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

    protected override async Task OnInitializedAsync()
    {
        // Saves List At Once, The Watcher Covers Explorer
        Controller.Library.Changed += OnLibraryChanged;
        Controller.Library.PicturesDone += OnPicturesDone;
        Controller.Library.PictureMade += OnPictureMade;
        Controller.Exports.Changed += OnExportsChanged;
        Controller.Recorder.ClipSaved += OnClipSaved;
        await LoadAsync();
    }

    private void OnPicturesDone()
    {
        // A Pass That Made Nothing Still Stops The Tiles' Spinners
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnPictureMade(string path, string picture)
    {
        _ = InvokeAsync(() =>
        {
            // Only That Card Changes, No Reload
            if (s_clips is { } clips)
            {
                s_clips =
                [
                    .. clips.Select(clip =>
                        clip.Path == path ? clip with { ThumbnailPath = picture } : clip
                    ),
                ];
                StateHasChanged();
            }
        });
    }

    private void OnExportsChanged()
    {
        // An Exporting Clip Cannot Be Deleted Meanwhile
        _ = InvokeAsync(StateHasChanged);
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
            _nextChange += () => s_cards = BatchSize;
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
        // Back Where It Was Left, Once Cached Cards Are Drawn
        if (firstRender && s_clips is not null && !s_fromTop)
        {
            await JavaScript.CallAsync("letsClip.restoreScroll");
        }

        s_fromTop = false;

        // Ponytail: Each Batch Rebuilds Every Shown Card, Fine To Thousands
        if (ShownClips.Count > s_cards)
        {
            await JavaScript.CallAsync("letsClip.watchEnd", Reference, nameof(ShowMoreCards));
        }
    }

    [JSInvokable]
    public void ShowMoreCards()
    {
        if (ShownClips.Count > s_cards)
        {
            s_cards += BatchSize;
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
            : IsFiltered || IsSearched ? $"{shown} of {clips.Count}"
            : $"{clips.Count}";
    }

    private static bool Matches(ClipInfo clip)
    {
        // The Name, The File Or The Game, Any Case
        var query = s_query?.Trim();
        return string.IsNullOrEmpty(query)
            || clip.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || clip.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || clip.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private async Task SearchAsync(string? query)
    {
        await ChangeAsync(() => s_query = query, fromTop: true);
    }

    private static string? CardStyle(ClipInfo clip)
    {
        // Its Own Name, So The Card Moves Rather Than Fades
        return s_cards > NamedCards
            ? null
            : $"view-transition-name:clip-{(uint)clip.Path.GetHashCode(StringComparison.OrdinalIgnoreCase)}";
    }

    private static IEnumerable<(string Label, string Value)> FactsOf(ClipInfo clip)
    {
        // The Editor's Order, Old Metadata May Lack Some
        yield return ("Size", Formatter.FormatSize(clip.SizeBytes));
        if (clip.Width > 0)
        {
            yield return ("Resolution", Formatter.FormatResolution(clip.Width, clip.Height));
        }

        if (clip.FrameRate > 0)
        {
            yield return ("Frame Rate", Formatter.FormatFrameRate(clip.FrameRate));
        }

        yield return ("Bitrate", Formatter.FormatBitrate(clip.SizeBytes * 8 / clip.Duration));
    }

    private static string FactsText(ClipInfo clip)
    {
        return string.Join(", ", FactsOf(clip).Select(fact => $"{fact.Label} {fact.Value}"));
    }

    #region Filter
    private void OpenFilter()
    {
        // Checks Answer At Once, The Cards Follow Through Their Fade
        _draft = new HashSet<string>(s_shownGames ?? [.. Games], StringComparer.OrdinalIgnoreCase);
        _draftUnedited = s_uneditedOnly;
        _search = null;
        _filterOpen = true;
    }

    private void CloseFilter()
    {
        _filterOpen = false;
    }

    private async Task SetGameAsync(string game, bool shown)
    {
        _ = shown ? _draft.Add(game) : _draft.Remove(game);
        await ApplyFilterAsync();
    }

    private async Task SetUneditedAsync(bool unedited)
    {
        _draftUnedited = unedited;
        await ApplyFilterAsync();
    }

    private async Task ResetFilterAsync()
    {
        _draft = new HashSet<string>(Games, StringComparer.OrdinalIgnoreCase);
        _draftUnedited = false;
        _search = null;
        await ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync()
    {
        // Every Game Checked Means No Filter, New Games Show Too
        var shown = Games.All(_draft.Contains)
            ? null
            : new HashSet<string>(_draft, StringComparer.OrdinalIgnoreCase);
        var unedited = _draftUnedited;
        await ChangeAsync(() => (s_shownGames, s_uneditedOnly) = (shown, unedited), fromTop: true);
    }

    private async Task ShowAllAsync()
    {
        await ChangeAsync(
            () => (s_shownGames, s_uneditedOnly, s_query) = (null, false, null),
            fromTop: true
        );
    }

    public static void ShowOnly(string source)
    {
        // A Game Chip Lists That Game Alone, From The Top
        s_shownGames =
            Games is [var only] && only.Equals(source, StringComparison.OrdinalIgnoreCase)
                ? null
                : new HashSet<string>([source], StringComparer.OrdinalIgnoreCase);
        s_uneditedOnly = false;
        s_query = null;
        s_cards = BatchSize;
        s_fromTop = true;
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

    private async Task RenameAsync(ClipInfo clip)
    {
        // The File Takes The Name, The Watcher Redraws Its Card
        if (
            await RenameDialog.AskAsync(DialogService, clip) is not { } name
            || clip.CustomTitleFrom(name) is var title && title == clip.CustomTitle
        )
        {
            return;
        }

        try
        {
            await Task.Run(() => ClipLibrary.RenameAsync(clip.Path, title));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Snackbar.Add("The clip could not be renamed, it may be in use", Severity.Warning);
        }
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
        Controller.Library.PicturesDone -= OnPicturesDone;
        Controller.Library.PictureMade -= OnPictureMade;
        Controller.Exports.Changed -= OnExportsChanged;
        Controller.Recorder.ClipSaved -= OnClipSaved;
        _closing.Cancel();
        _closing.Dispose();
        _reference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
