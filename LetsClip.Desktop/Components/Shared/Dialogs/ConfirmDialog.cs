using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public static class ConfirmDialog
{
    public static async Task<bool> ConfirmDeleteAsync(this IDialogService dialogService)
    {
        var dialog = await dialogService.ShowAsync<YesNoDialog>(
            "Delete Clip",
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall }
        );

        return await dialog.Result is { Canceled: false };
    }
}
