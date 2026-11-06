using LetsClip.Desktop.Interop;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class AboutDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    private static void OpenNotices()
    {
        // The Single File Unpacks Notices Beside The App
        Explorer.Open(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
    }
}
