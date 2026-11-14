using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class ChangeList
{
    // Commit Titles, Newest First
    [Parameter]
    public required IReadOnlyList<string> Changes { get; set; }
}
