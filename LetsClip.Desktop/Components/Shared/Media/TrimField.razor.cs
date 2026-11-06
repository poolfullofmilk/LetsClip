using LetsClip.Data.Helpers;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class TrimField
{
    [Parameter]
    public required string Label { get; set; }

    [Parameter]
    public required string Icon { get; set; }

    [Parameter]
    public double Value { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public EventCallback<double> OnCommit { get; set; }

    // Moves The Point To The Frame On Screen
    [Parameter]
    public EventCallback OnSet { get; set; }

    // End Keeps Its Button Toward The Timeline's Middle
    [Parameter]
    public bool ButtonFirst { get; set; }

    private int _version;

    private async Task CommitAsync(string text)
    {
        // Enter Or Leaving Commits, A Typo Restores The Last Time
        _version++;
        if (Formatter.ParseDuration(text) is { } seconds)
        {
            await OnCommit.InvokeAsync(seconds);
        }
    }
}
