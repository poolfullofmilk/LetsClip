using LetsClip.Desktop.Components.Shared.Common;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NLog;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class MoveDialog
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Parameter]
    public required string From { get; set; }

    [Parameter]
    public required string To { get; set; }

    private TransferProgress? _progress;

    public static async Task<(int Moved, int Failed)?> MoveAsync(
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
        return await dialog.Result is { Canceled: false, Data: ValueTuple<int, int> result }
            ? result
            : null;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        // Off The Page Thread, The Bar Follows Each Piece
        var progress = new Progress<(long Moved, long Total)>(moved => _progress?.Report(moved));
        try
        {
            var result = await Task.Run(() => ClipLibrary.MoveAllAsync(From, To, progress));
            MudDialog.Close(DialogResult.Ok(result));
        }
        catch (Exception exception)
        {
            // An Unforeseen Failure Never Leaves The Dialog Open
            s_logger.Warn($"Failed To Move The Clips {exception.GetType().Name}");
            MudDialog.Cancel();
        }
    }
}
