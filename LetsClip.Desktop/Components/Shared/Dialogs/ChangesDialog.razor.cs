using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class ChangesDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Parameter]
    public required IReadOnlyList<string> Changes { get; set; }
}
