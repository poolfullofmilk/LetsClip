using LetsClip.Desktop.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using NLog;

namespace LetsClip.Desktop.Components.Layout;

public sealed class SelfHealingBoundary : ErrorBoundary
{
    // A Page Failing Again This Soon Shows Its Panel
    private static readonly TimeSpan s_quietGap = TimeSpan.FromSeconds(10);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private DateTime _lastHealed;

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    protected override Task OnErrorAsync(Exception exception)
    {
        s_logger.Error(exception, "Failed In A Page");
        if (DateTime.UtcNow - _lastHealed < s_quietGap)
        {
            return Task.CompletedTask;
        }

        // Back To The Clips, Once The Boundary Holds The Error
        _lastHealed = DateTime.UtcNow;
        _ = InvokeAsync(async () =>
        {
            await Task.Yield();
            BackToClips();
        });
        return Task.CompletedTask;
    }

    public void BackToClips()
    {
        if (NavigationManager.ToBaseRelativePath(NavigationManager.Uri) is "" or Clips.Route)
        {
            Recover();
            return;
        }

        NavigationManager.LocationChanged += OnArrived;
        NavigationManager.NavigateTo(Clips.Route);
    }

    private void OnArrived(object? sender, LocationChangedEventArgs eventArgs)
    {
        // Only Now, The Failed Page Would Throw Again
        NavigationManager.LocationChanged -= OnArrived;
        _ = InvokeAsync(Recover);
    }
}
