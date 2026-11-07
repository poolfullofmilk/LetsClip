using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class ItemLabel
{
    [Parameter]
    public required string Text { get; set; }

    // Heavier Choices Read Amber Or Red
    [Parameter]
    public Color Color { get; set; } = Color.Default;

    // Greyed On The Right, Shortened Before The Text
    [Parameter]
    public string? Detail { get; set; }

    // Null Leaves No Room For A Check Mark
    [Parameter]
    public bool? Checked { get; set; }
}
