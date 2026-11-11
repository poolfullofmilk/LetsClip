using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Games;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class GamesDialog : IDisposable
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    // Rows Keep Their Order, A Switch Never Moves One
    private List<AddedGame> _rows = [];
    private IReadOnlyList<AddedGame>? _apps;
    private Dictionary<string, GameEntry> _listed = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, string> _icons = [with(StringComparer.OrdinalIgnoreCase)];
    private string? _search;
    private bool _showApps;

    private bool IsLoading => _apps is null || !Controller.GamesScanned;

    // Settings And The Capture Panel Open It Alike
    public static Task ShowAsync(IDialogService dialogService)
    {
        return dialogService.ShowAsync<GamesDialog>(
            (string?)null,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true }
        );
    }

    // Library Games Under Games, Everything Else Under Apps
    private IReadOnlyList<AddedGame> ShownRows =>
        [
            .. _rows.Where(row =>
                IsInstalledGame(row) != _showApps
                && (
                    string.IsNullOrWhiteSpace(_search)
                    || row.Name.Contains(_search.Trim(), StringComparison.CurrentCultureIgnoreCase)
                )
            ),
        ];

    protected override async Task OnInitializedAsync()
    {
        Controller.Changed += OnChanged;
        _apps = await Task.Run(GameWatcher.FindApps);
        await RefreshAsync();
    }

    private void OnChanged()
    {
        _ = InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        });
    }

    private async Task RefreshAsync()
    {
        // Rows Wait For The Library Scan And The Apps
        if (_apps is not { } apps || !Controller.GamesScanned)
        {
            return;
        }

        _listed = Controller
            .GameList()
            .ToDictionary(game => game.Key, StringComparer.OrdinalIgnoreCase);
        var paths = apps.Where(app => app.Path is not null)
            .DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(app => app.Key, app => app.Path, StringComparer.OrdinalIgnoreCase);
        var shown = _rows.Select(row => row.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // New Rows Go On Top, Recorded First, Then By Name
        var fresh = _listed
            .Values.Select(game => new AddedGame(
                game.Key,
                game.Name,
                game.Path ?? paths.GetValueOrDefault(game.Key)
            ))
            .Concat(apps.Where(app => !_listed.ContainsKey(app.Key)))
            .Where(row => !shown.Contains(row.Key))
            .OrderBy(row => !IsRecorded(row))
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _rows = [.. fresh, .. _rows];
        if (fresh.Count == 0)
        {
            return;
        }

        // Icons Are Read Once, Then Kept Beside The Thumbnails
        var icons = await Task.Run(() =>
            fresh.Select(row => (row.Key, Url: DesktopShell.IconUrl(row.Path))).ToList()
        );
        foreach (var (key, url) in icons)
        {
            _icons[key] = url;
        }
    }

    private bool IsRecorded(AddedGame row)
    {
        return _listed.TryGetValue(row.Key, out var game) && !game.IsRemoved;
    }

    private bool IsInstalledGame(AddedGame row)
    {
        return _listed.TryGetValue(row.Key, out var game) && game.IsInstalledGame;
    }

    private void SetRecorded(AddedGame row, bool on)
    {
        Controller.SetRecorded([row], on);
    }

    // Every Shown Row At Once, None Once All Are On
    private bool AllShownRecorded => ShownRows.All(IsRecorded);

    private void ToggleAll()
    {
        Controller.SetRecorded(ShownRows, !AllShownRecorded);
    }

    private void BrowseForGame()
    {
        if (DesktopShell.PickGame() is not { } path)
        {
            return;
        }

        // The Added Exe Shows Where It Lands
        var game = GameWatcher.GameOf(path);
        _showApps = !IsInstalledGame(game);
        Controller.SetRecorded([game], true);
    }

    public void Dispose()
    {
        Controller.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
