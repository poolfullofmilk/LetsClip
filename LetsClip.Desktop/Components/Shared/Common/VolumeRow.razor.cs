using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class VolumeRow : IDisposable
{
    // Applied Once The Slider Rests, A Burst Restarts Nothing Twice
    private static readonly TimeSpan s_settle = TimeSpan.FromMilliseconds(400);

    [Parameter]
    public required string Title { get; set; }

    [Parameter]
    public double Value { get; set; }

    [Parameter]
    public EventCallback<double> ValueChanged { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    private Timer? _timer;
    private double? _slid;

    private double Shown => _slid ?? Value;

    // A Greyed Slider Says Why, Its Track Keeps The Level
    private string ShownText => Disabled ? "Not Used" : $"{Shown * 100:0}%";

    private void Slide(double level)
    {
        // Shown At Once, Applied Once The Slider Rests
        _slid = Math.Round(level, 1);
        _timer ??= new Timer(_ => _ = InvokeAsync(ApplyAsync));
        _timer.Change(s_settle, Timeout.InfiniteTimeSpan);
    }

    private async Task ApplyAsync()
    {
        if (_slid is not { } level)
        {
            return;
        }

        _slid = null;
        await ValueChanged.InvokeAsync(level);
    }

    public void Dispose()
    {
        // A Volume Slid Just Before Leaving Still Lands
        _timer?.Dispose();
        _ = ApplyAsync();
        GC.SuppressFinalize(this);
    }
}
