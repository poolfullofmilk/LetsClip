using LetsClip.Data.ViewModels;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class YesNoDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Parameter]
    public required string Question { get; set; }

    // The Clips It Touches, Listed Under The Question
    [Parameter]
    public IReadOnlyList<ClipInfo>? Clips { get; set; }

    public static async Task<bool> ConfirmAsync(
        IDialogService dialogService,
        string title,
        string question,
        IReadOnlyList<ClipInfo>? clips = null
    )
    {
        // Every Removal Asks First, Yes Reads Red
        var dialog = await dialogService.ShowAsync<YesNoDialog>(
            title,
            new DialogParameters<YesNoDialog>
            {
                { yesNo => yesNo.Question, question },
                { yesNo => yesNo.Clips, clips },
            },
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
