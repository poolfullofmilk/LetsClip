using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class GamesDialog : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    private IReadOnlyList<GameEntry> _games = [];

    protected override void OnInitialized()
    {
        Controller.Changed += OnChanged;
        _games = Controller.GameList();
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

    private void AddGame()
    {
        if (DesktopShell.PickGame() is { } path)
        {
            Controller.AddGame(path);
        }
    }

    public void Dispose()
    {
        Controller.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
