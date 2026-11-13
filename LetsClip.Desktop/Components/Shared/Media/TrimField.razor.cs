using LetsClip.Data.Helpers;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class TrimField
{
    // Start Or End, The Page Script's Key
    [Parameter]
    public required string Edge { get; set; }

    // Says What The Time Does, Not Only Which End
    private string Label => $"Cut {Edge}";

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

    // One Step Later Or Earlier, Like A Handle's Arrow Key
    [Parameter]
    public EventCallback<int> OnStep { get; set; }

    private async Task CommitAsync(string text)
    {
        // Placed On Enter Or Leaving, A Typo Restores The Time
        await OnCommit.InvokeAsync(Formatter.ParseDuration(text) ?? Value);
    }
}
