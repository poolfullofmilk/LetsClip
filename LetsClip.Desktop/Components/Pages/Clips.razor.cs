using System.ComponentModel;
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

    // Deleted Clips Wait Out Their Undo, Then Get Recycled
    private static readonly HashSet<string> s_deleting = [with(StringComparer.OrdinalIgnoreCase)];

    private HashSet<string> _draft = [];
    private bool _draftUnedited;
    private string? _search;
    private bool _filterOpen;
    private DateTimeOffset? _seenAt;

    private DotNetObjectReference<Clips> Reference =>
        _reference ??= DotNetObjectReference.Create(this);

    private static bool IsFiltered => s_shownGames is not null || s_uneditedOnly;

    private static bool IsSearched => !string.IsNullOrWhiteSpace(s_query);

    private static int UneditedCount => s_clips?.Count(clip => !clip.IsEdited) ?? 0;

    // A Tick Must Change The Cards, Unticking Always Can
    private bool UneditedDisabled =>
        !_draftUnedited && UneditedCount is var count && (count == 0 || count == s_clips?.Count);

    // A Lone Source Has Nothing To Pick Between
    private static bool OneSource => Games.Count < 2;

    // Video Left Out With Sound Kept, Discord Gets Opus
    private bool SoundOnly =>
        !Controller.Settings.ExportVideo
        && (Controller.Settings.ExportAudio || Controller.Settings.ExportMicrophone);

    // Sorted By Time, Clips Sit Under Day, Week Or Month
    private bool IsGrouped => Controller.Settings.ClipSort is ClipSort.Newest or ClipSort.Oldest;

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

    private static event Action? DeleteUndone;

    protected override async Task OnInitializedAsync()
    {
        // A Clip Deleted In The Editor Never Flashes Back
        if (s_clips is not null)
        {
            s_clips = Kept(s_clips);
        }

        _seenAt = Controller.Settings.ClipsSeenAt;
        Controller.Changed += OnControllerChanged;
        DeleteUndone += OnLibraryChanged;

        // Saves List At Once, The Watcher Covers Explorer
        Controller.Library.Changed += OnLibraryChanged;
        Controller.Library.PicturesDone += OnPicturesDone;
        Controller.Library.PictureMade += OnPictureMade;
        Controller.Exports.Changed += OnExportsChanged;
        Controller.Exports.Finished += OnExportFinished;
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

    private void OnControllerChanged()
    {
        // Hidden Again, Its Dots Go Before The Next Look
        if (Controller.Settings.ClipsSeenAt != _seenAt)
        {
            _seenAt = Controller.Settings.ClipsSeenAt;
            _ = InvokeAsync(StateHasChanged);
        }
    }

    private void OnExportsChanged()
    {
        // An Exporting Clip Cannot Be Deleted Meanwhile
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnExportFinished(ExportOutcome outcome)
    {
        // Its Card Offers The New Export At Once
        _ = InvokeAsync(LoadAsync);
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

    private async Task ShowAsync(IReadOnlyList<ClipInfo> listed)
    {
        // The Same Cards Just Update
        var clips = Kept(listed);
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
        // A Card Dragged Out Asks This Page For Its File
        if (firstRender)
        {
            await JavaScript.CallAsync("letsClip.dragClips", Reference);
        }

        // Back Where It Was Left, Once Cached Cards Are Drawn
        if (firstRender && s_clips is not null && !s_fromTop)
        {
            await JavaScript.CallAsync("letsClip.restoreScroll");
        }

        s_fromTop = false;

        // The Cards Drawn Get Their Pictures Before The Rest
        Controller.Library.ShowFirst(ShownClips.Take(s_cards).Select(clip => clip.Path));

        // Ponytail: Each Batch Diffs Every Shown Card, Fine To Thousands
        if (ShownClips.Count > s_cards)
        {
            await JavaScript.CallAsync("letsClip.watchEnd", Reference, nameof(ShowMoreCards));
        }
    }

    [JSInvokable]
    public void DragFile(string path)
    {
        // Only A Shown Card's Clip, No Other File Is Named
        if (ShownClips.Any(clip => clip.Path == path))
        {
            DesktopShell.DragFile(path);
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

    private static string GroupOf(ClipInfo clip)
    {
        return Formatter.FormatGroup(clip.RecordedAt, DateTime.Today);
    }

    private Dictionary<string, int> GroupCounts(IReadOnlyList<ClipInfo> shown)
    {
        // Each Header Counts Its Shown Clips, Drawn Or Not
        return IsGrouped ? shown.CountBy(GroupOf).ToDictionary() : [];
    }

    private string TimeOf(ClipInfo clip)
    {
        // Today Or Yesterday Shows The Time, Elsewhere The Date Too
        return IsGrouped && clip.RecordedAt.LocalDateTime.Date >= DateTime.Today.AddDays(-1)
            ? Formatter.FormatTime(clip.RecordedAt)
            : Formatter.FormatDateTime(clip.RecordedAt);
    }

    private async Task CopyForDiscordAsync(ClipInfo clip, DiscordPlan plan)
    {
        // The Kept Selection And Sound, On The Clipboard Once Ready
        Controller.UpdateSettings(settings => settings with { ExportDiscordPlan = plan });
        MediaInfo? media;
        try
        {
            media = await Task.Run(() =>
                MediaProbe.ProbeAsync(clip.Path, CancellationToken.None, withPackets: true)
            );
        }
        catch (Exception exception) when (exception is TimeoutException or Win32Exception)
        {
            media = null;
        }

        if (media is not { HasVideo: true, Duration: > 0 })
        {
            Snackbar.Add("The clip could not be read, it may be in use", Severity.Warning);
            return;
        }

        var metadata = ClipLibrary.MetadataOf(clip.Path);
        var range = metadata is { TrimStart: { } start, TrimEnd: { } end }
            ? TrimRange.Fit(start, end, media.Duration)
            : TrimRange.Whole(media.Duration);
        var settings = Controller.Settings;
        var copy = ClipExporter.ForDiscord(
            clip,
            media,
            range,
            plan,
            settings.ExportAudio,
            settings.ExportMicrophone,
            settings.ExportVideo,
            settings.ExportsDirectory,
            metadata?.Box,
            settings.ExportGameVolume,
            settings.ExportMicrophoneVolume
        );
        if (copy.Problem is { } problem)
        {
            Snackbar.Add(problem, Severity.Warning);
        }
        else if (copy.Request is { } request)
        {
            Snackbar.Add(
                $"Exporting for {SizeBudget.NameOf(plan)}, copied once it is ready",
                Severity.Info
            );
            _ = Controller.Exports.RunAsync(request, saveCut: false, keepOriginal: true);
        }
        else
        {
            DesktopShell.CopyFile(clip.Path, Snackbar);
        }
    }

    private bool IsNew(ClipInfo clip)
    {
        // Saved Since LetsClip Was Last Hidden
        return clip.RecordedAt > Controller.Settings.ClipsSeenAt;
    }

    private static int CountOf(string source)
    {
        return s_clips?.Count(clip =>
                clip.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
            )
            ?? 0;
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

    public static void Replaced(string path)
    {
        // Its Old Picture Is Gone Until The Next List
        s_clips = s_clips
            ?.Select(clip => clip.Path == path ? clip with { ThumbnailPath = null } : clip)
            .ToList();
    }

    public static string EditorOf(string path)
    {
        return $"{Route}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }

    private void Open(ClipInfo clip)
    {
        // Coming Back Draws To This Clip And One Batch More
        var index = ShownClips.ToList().FindIndex(shown => shown.Path == clip.Path);
        s_cards = Math.Min(s_cards, ((Math.Max(index, 0) / BatchSize) + 2) * BatchSize);
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
            var renamed = await Task.Run(() => ClipLibrary.RenameAsync(clip.Path, title));
            Controller.Exports.Renamed(clip.Path, renamed);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Snackbar.Add("The clip could not be renamed, it may be in use", Severity.Warning);
        }
    }

    private async Task DeleteAsync(ClipInfo clip)
    {
        // The Card Goes At Once, Undo Brings It Back
        DeleteLater(clip.Path, Snackbar);
        ++_loads;
        await ShowAsync(s_clips ?? []);
    }

    #region Deletes
    public static void DeleteLater(string path, ISnackbar snackbar)
    {
        // Gone From The List At Once, Recycled After Undo
        s_deleting.Add(path);
        var undone = false;
        var shown = snackbar.Add(
            "Deleted the clip",
            Severity.Success,
            options =>
            {
                // A Button Alone Would Keep It Open Until Pressed
                options.RequireInteraction = false;
                options.Action = "Undo";
                options.OnClick = _ =>
                {
                    undone = true;
                    if (s_deleting.Remove(path))
                    {
                        DeleteUndone?.Invoke();
                    }

                    return Task.CompletedTask;
                };
            },
            path
        );
        if (shown is null)
        {
            Recycle(path, snackbar);
            return;
        }

        // Closed On A Timer Thread, Recycled On The Window's
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        shown.OnClose += _ =>
            dispatcher.InvokeAsync(() =>
            {
                if (!undone)
                {
                    Recycle(path, snackbar);
                }
            });
    }

    private static void Recycle(string path, ISnackbar snackbar)
    {
        // Exit May Have Recycled It, Or The File Moved
        if (!s_deleting.Remove(path) || !File.Exists(path) || ClipLibrary.RecycleClip(path))
        {
            return;
        }

        snackbar.Add("The clip could not be deleted, it may be in use", Severity.Warning);
        DeleteUndone?.Invoke();
    }

    public static void FinishDeletes()
    {
        // Exiting Never Waits Out An Undo
        foreach (var path in s_deleting.ToList())
        {
            s_deleting.Remove(path);
            _ = ClipLibrary.RecycleClip(path);
        }
    }

    private static IReadOnlyList<ClipInfo> Kept(IReadOnlyList<ClipInfo> clips)
    {
        return s_deleting.Count == 0
            ? clips
            : [.. clips.Where(clip => !s_deleting.Contains(clip.Path))];
    }
    #endregion

    private void OpenFolder()
    {
        Explorer.OpenFolder(Controller.Settings.ClipsDirectory);
    }

    public void Dispose()
    {
        Controller.Changed -= OnControllerChanged;
        DeleteUndone -= OnLibraryChanged;
        Controller.Library.Changed -= OnLibraryChanged;
        Controller.Library.PicturesDone -= OnPicturesDone;
        Controller.Library.PictureMade -= OnPictureMade;
        Controller.Exports.Changed -= OnExportsChanged;
        Controller.Exports.Finished -= OnExportFinished;
        Controller.Recorder.ClipSaved -= OnClipSaved;
        _closing.Cancel();
        _closing.Dispose();
        _reference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
