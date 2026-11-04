using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Games;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class GamesDialog : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    private IReadOnlyList<GameEntry> _games = [];

    // Open Apps Not Listed Yet, Read When The Dialog Opens
    private IReadOnlyList<AddedGame> RunningApps
    {
        get => [.. field.Where(app => !_games.Any(game => !game.IsRemoved && game.Key == app.Key))];
        set;
    } = [];

    protected override async Task OnInitializedAsync()
    {
        Controller.Changed += OnChanged;
        _games = Controller.GameList();
        RunningApps = await Task.Run(GameWatcher.RunningApps);
    }

    private void OnChanged()
    {
        _ = InvokeAsync(() =>
        {
            _games = Controller.GameList();
            StateHasChanged();
        });
    }

    private static string? NameClass(GameEntry game)
    {
        return game.IsRemoved ? "mud-text-disabled" : null;
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
