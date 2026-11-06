using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class DialogTitle
{
    // MudBlazor Renders Titles Outside The Dialog's Cascade
    [Parameter]
    public required IMudDialogInstance Dialog { get; set; }

    [Parameter]
    public required string Text { get; set; }

    // MudBlazor's Own Close Button Has No Rounded Icon
    [Parameter]
    public bool Closable { get; set; } = true;
}
