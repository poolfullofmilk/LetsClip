using LetsClip.Desktop.Components.Shared.Dialogs;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class EditGamesButton
{
    [Inject]
    public required IDialogService DialogService { get; set; }

    private Task OpenAsync()
    {
        return GamesDialog.ShowAsync(DialogService);
    }
}
