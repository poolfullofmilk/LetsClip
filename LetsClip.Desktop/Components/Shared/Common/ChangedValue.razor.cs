using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class ChangedValue
{
    // What The Export Gets
    [Parameter]
    public required string Value { get; set; }

    // The Clip's Own, Struck Through Red When The Export Differs
    [Parameter]
    public required string Was { get; set; }

    // Grey Words Like Up To, Shown Only With The Strike
    [Parameter]
    public string? Prefix { get; set; }

    // On The Value Itself, For The Page Script's Live Drag
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? Attributes { get; set; }

    private bool IsChanged => Was != Value;
}
