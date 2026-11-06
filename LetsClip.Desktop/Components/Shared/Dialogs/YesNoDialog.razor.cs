using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class YesNoDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    private void Yes()
    {
        MudDialog.Close(DialogResult.Ok(true));
    }

    private void No()
    {
        MudDialog.Cancel();
    }
}
