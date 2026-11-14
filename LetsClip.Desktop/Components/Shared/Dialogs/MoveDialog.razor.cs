using LetsClip.Desktop.Components.Shared.Common;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class MoveDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Parameter]
    public required string From { get; set; }

    [Parameter]
    public required string To { get; set; }

    private TransferProgress? _progress;

    public static async Task<(int Moved, int Failed)> MoveAsync(
        IDialogService dialogService,
        string from,
        string to
    )
    {
        // Open Until Every File Moved, Nothing Closes It Halfway
        var dialog = await dialogService.ShowAsync<MoveDialog>(
            null,
            new DialogParameters<MoveDialog>
            {
                { move => move.From, from },
                { move => move.To, to },
            },
            new DialogOptions
            {
                MaxWidth = MaxWidth.ExtraSmall,
                FullWidth = true,
                BackdropClick = false,
                CloseOnEscapeKey = false,
            }
        );
        return await dialog.Result is { Data: ValueTuple<int, int> result } ? result : (0, 0);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        // Off The Page Thread, The Bar Follows Each Piece
        var progress = new Progress<(long Moved, long Total)>(moved => _progress?.Report(moved));
        var result = await Task.Run(() => ClipLibrary.MoveAllAsync(From, To, progress));
        MudDialog.Close(DialogResult.Ok(result));
    }
}
