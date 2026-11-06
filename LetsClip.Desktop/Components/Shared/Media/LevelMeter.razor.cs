using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class LevelMeter : IDisposable
{
    // Reads 0 To 1, Each Reading Since The Last
    [Parameter]
    public required Func<double> Read { get; set; }

    private readonly CancellationTokenSource _stop = new();
    private double _level;

    protected override void OnInitialized()
    {
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        // Twenty Readings A Second, Only This Bar Renders
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var token = _stop.Token;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                _level = Read();
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // The Test Ended
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
