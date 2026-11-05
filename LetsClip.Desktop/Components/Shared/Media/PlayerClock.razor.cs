using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class PlayerClock
{
    [Parameter]
    public double Start { get; set; }

    [Parameter]
    public double Duration { get; set; }

    private double _time;

    protected override void OnInitialized()
    {
        _time = Start;
    }

    public void Show(double time)
    {
        // Only This Label Renders Ten Times A Second
        _time = time;
        StateHasChanged();
    }
}
