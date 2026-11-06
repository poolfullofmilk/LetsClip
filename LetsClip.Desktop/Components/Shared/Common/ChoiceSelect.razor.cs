using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class ChoiceSelect<T>
{
    [Parameter]
    public T? Value { get; set; }

    [Parameter]
    public EventCallback<T?> ValueChanged { get; set; }

    [Parameter]
    public Func<T?, string?>? ToStringFunc { get; set; }

    [Parameter]
    public EventCallback<bool> OpenChanged { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // Label, Disabled And Placeholder Reach The Select As Given
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? Attributes { get; set; }
}
