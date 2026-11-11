using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class IconButton
{
    [Parameter]
    public required string Icon { get; set; }

    // The Tooltip And The Name Screen Readers Hear
    [Parameter]
    public required string Text { get; set; }

    [Parameter]
    public Placement Placement { get; set; } = Placement.Top;

    [Parameter]
    public Color Color { get; set; } = Color.Default;

    [Parameter]
    public bool Filled { get; set; }

    // A Full Button's Height And Corners, For The Player
    [Parameter]
    public bool Square { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    // A Spinner In The Icon's Place While Its Work Runs
    [Parameter]
    public bool Busy { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    private static readonly RenderFragment s_spinner = builder =>
    {
        builder.OpenComponent<MudProgressCircular>(0);
        builder.AddComponentParameter(1, nameof(MudProgressCircular.Indeterminate), true);
        builder.AddComponentParameter(2, nameof(MudProgressCircular.Class), "button-spinner");
        builder.CloseComponent();
    };

    private Task PressAsync()
    {
        // A Press While It Works Does Nothing
        return Busy ? Task.CompletedTask : OnClick.InvokeAsync();
    }
}
