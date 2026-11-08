using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class YesNoDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    public static async Task<bool> ConfirmDeleteAsync(IDialogService dialogService)
    {
        var dialog = await dialogService.ShowAsync<YesNoDialog>(
            "Delete Clip",
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall }
        );

        return await dialog.Result is { Canceled: false };
    }

    private void Yes()
    {
        MudDialog.Close(DialogResult.Ok(true));
    }

    private void No()
    {
        MudDialog.Cancel();
    }
}
