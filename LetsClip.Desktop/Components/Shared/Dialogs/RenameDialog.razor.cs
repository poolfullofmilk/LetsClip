using LetsClip.Data.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class RenameDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Parameter]
    public required ClipInfo Clip { get; set; }

    private MudTextField<string>? _field;
    private string _name = "";

    protected override void OnInitialized()
    {
        _name = Clip.CustomTitle ?? "";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Ready To Type At Once
        if (firstRender && _field is { } field)
        {
            await field.FocusAsync();
        }
    }

    public static async Task<string?> AskAsync(IDialogService dialogService, ClipInfo clip)
    {
        // The New Name, Empty For The Default, Null When Cancelled
        var dialog = await dialogService.ShowAsync<RenameDialog>(
            "Rename Clip",
            new DialogParameters<RenameDialog> { { rename => rename.Clip, clip } },
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true }
        );
        return await dialog.Result is { Canceled: false, Data: string name } ? name : null;
    }

    private async Task KeyAsync(KeyboardEventArgs key)
    {
        // Enter Saves, Escape Cancels Like Any Dialog
        if (key.Key == "Enter")
        {
            await Task.Yield();
            Save();
        }
    }

    private void Save()
    {
        MudDialog.Close(DialogResult.Ok(_name.Trim()));
    }
}
