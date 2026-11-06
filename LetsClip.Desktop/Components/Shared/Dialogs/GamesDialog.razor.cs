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
    private Dictionary<string, GameEntry> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    private string? _search;

    private bool IsLoading => _apps is null || !Controller.GamesScanned;

    private IReadOnlyList<AddedGame> ShownRows =>
        string.IsNullOrWhiteSpace(_search)
            ? _rows
            :
            [
                .. _rows.Where(row =>
                    row.Name.Contains(_search.Trim(), StringComparison.CurrentCultureIgnoreCase)
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
            fresh.Select(row => (row.Key, Url: AppIcons.UrlOf(row.Path))).ToList()
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

    private void SetRecorded(AddedGame row, bool on)
    {
        // Listed Ones Switch In Place, An App Joins The List
        if (_listed.ContainsKey(row.Key))
        {
            Controller.SetRemoved(row.Key, !on);
        }
        else if (on)
        {
            Controller.AddGame(row);
        }
    }

    private void BrowseForGame()
    {
        if (DesktopShell.PickGame() is { } path)
        {
            Controller.AddGame(GameWatcher.GameOf(path));
        }
    }

    public void Dispose()
    {
        Controller.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
