using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Pages;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class YesNoDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Inject]
    public required NavigationManager Navigation { get; set; }

    [Parameter]
    public required string Question { get; set; }

    // The Clips It Touches, Listed Under The Question
    [Parameter]
    public IReadOnlyList<ClipInfo>? Clips { get; set; }

    [Parameter]
    public (string Text, string Route)? Link { get; set; }

    public static Task<bool> ConfirmDeleteAsync(IDialogService dialogService)
    {
        return ConfirmAsync(
            dialogService,
            "Delete Clip",
            "It goes to the Recycle Bin, its exports stay"
        );
    }

    public static Task<bool> ConfirmCutAsync(IDialogService dialogService, string title)
    {
        // Without Kept Originals, A Cut Cannot Be Undone
        return ConfirmAsync(
            dialogService,
            title,
            "The parts outside the selection are deleted forever",
            ("Keep Originals in Settings", Settings.StorageRoute)
        );
    }

    public static async Task<bool> ConfirmAsync(
        IDialogService dialogService,
        string title,
        string question,
        (string Text, string Route)? link = null,
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
                { yesNo => yesNo.Link, link },
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

    private void Follow(string route)
    {
        // Leaving To Change It Answers No
        MudDialog.Cancel();
        Navigation.NavigateTo(route);
    }
}
