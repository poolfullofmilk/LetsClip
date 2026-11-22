using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class FormPopover
{
    // MudOverlay's Own Layer, Under The Page's Popovers
    private const int PageLayer = 5;

    private static readonly ZIndex s_layers = new();

    // In A Dialog, Above It And Under The Popover
    [CascadingParameter]
    public IMudDialogInstance? Dialog { get; set; }

    private int OverlayLayer => Dialog is null ? PageLayer : s_layers.Dialog + 2;

    [Parameter]
    public bool Open { get; set; }

    // An Outside Click Closes It
    [Parameter]
    public EventCallback Closed { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // Reset Or A Quiet Action Left, The Primary Right
    [Parameter]
    public RenderFragment? Footer { get; set; }
}
