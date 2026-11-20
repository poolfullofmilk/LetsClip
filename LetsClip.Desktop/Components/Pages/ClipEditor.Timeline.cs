using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Media;
using Microsoft.JSInterop;

namespace LetsClip.Desktop.Components.Pages;

public partial class ClipEditor
{
    #region Player Calls
    [JSInvokable]
    public void OnPlayerTime(double time, bool paused)
    {
        // Only The Clock Follows Playback, Not The Whole Editor
        _clock?.Show(time);
        if (paused != _paused)
        {
            _paused = paused;
            StateHasChanged();
        }
    }

    [JSInvokable]
    public void OnRange(double start, double end)
    {
        // The Page Script Dragged The Handles, Here The Range Settles
        SetRange(TrimRange.Fit(start, end, _duration));
    }

    [JSInvokable]
    public void OnPlayerReady()
    {
        // Controls Wait For The First Frame
        (_ready, _placeCrop) = (true, _crop is not null);
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnView(double start, double end)
    {
        // Settled After The Wheel, Blazor Draws The Same Window
        (_viewStart, _viewEnd) = (start, end);
        StateHasChanged();
        if (_path is not { } path || end - start >= _duration - 0.001)
        {
            return;
        }

        // A Zoomed Window Gets Its Own Strip, The Newest Wins
        var zoom = ++_zooms;
        var (duration, token) = (_duration, _lifetime?.Token ?? CancellationToken.None);
        var window = new TrimRange(start, end);
        var separate = _media?.HasSeparateTracks == true;
        try
        {
            // The Strip And The Outline Are Made Side By Side
            var making = Task.Run(
                () => ClipLibrary.EnsureStripAsync(path, duration, token, window),
                token
            );
            var wave =
                _media?.HasAudio == true
                    ? await Task.Run(
                        () => ClipLibrary.EnsureWaveAsync(path, duration, separate, token, window),
                        token
                    )
                    : null;
            var strip = await making;
            if (zoom == _zooms && strip is not null && !string.IsNullOrEmpty(_strip.Id))
            {
                await JavaScript.CallAsync(
                    "letsClip.showZoom",
                    _strip,
                    DesktopShell.ThumbnailUrl(strip),
                    wave is null ? null : DesktopShell.ThumbnailUrl(wave),
                    start,
                    end
                );
            }
        }
        catch (OperationCanceledException)
        {
            // The Stretched Whole Clip Strip Stays
        }
    }

    [JSInvokable]
    public void OnCrop(double center)
    {
        // Dragged In The Page Script, Remembered With The Clip
        _crop = Math.Clamp(center, 0, 1);
        SaveCrop();
        StateHasChanged();
    }

    private void OnStreamsChanged()
    {
        // A Crop Box Shown Again Is Placed Again
        _placeCrop = _crop is not null;
    }

    private void SetVertical(bool vertical)
    {
        (_crop, _placeCrop) = (vertical ? 0.5 : null, vertical);
        SaveCrop();
    }

    private void SaveCrop()
    {
        if (_path is { } path)
        {
            var crop = _crop;
            _ = Task.Run(() =>
                ClipLibrary.UpdateMetadata(path, metadata => metadata with { Crop = crop })
            );
        }
    }

    [JSInvokable]
    public void OnPlayerError()
    {
        (_playError, _ready) = (true, false);
        StateHasChanged();
    }
    #endregion

    #region Trim
    private void SetRange(TrimRange range)
    {
        if (!range.IsValidFor(_duration) || range == _range)
        {
            return;
        }

        _range = range;
        StateHasChanged();
        ScheduleTrimSave();
    }

    private async Task MarkAsync(string edge)
    {
        // The Page Script Knows The Frame On Screen
        await JavaScript.CallAsync("letsClip.mark", edge);
    }

    private async Task PlaceAsync(string edge, double seconds)
    {
        // A Typed Point Snaps To Its Frame Like A Drag
        await JavaScript.CallAsync("letsClip.place", edge, seconds);
    }

    private async Task StepAsync(string edge, int direction)
    {
        await JavaScript.CallAsync("letsClip.stepEdge", edge, direction);
    }

    private void ScheduleTrimSave()
    {
        // The Selection Survives A Restart, Written Once Dragging Settles
        if (_path is not { } path)
        {
            return;
        }

        var range = _range == TrimRange.Whole(_duration) ? (TrimRange?)null : _range;
        Volatile.Write(
            ref _pendingTrim,
            () =>
                ClipLibrary.UpdateMetadata(
                    path,
                    metadata => metadata with { TrimStart = range?.Start, TrimEnd = range?.End }
                )
        );
        SaveSoon();
    }

    private void SaveSoon()
    {
        _saveTimer ??= new Timer(
            _ => SaveNow(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _saveTimer.Change(s_saveDelay, Timeout.InfiniteTimeSpan);
    }

    private void SaveNow()
    {
        // Taken Once, So Nothing Is Written Twice
        Interlocked.Exchange(ref _pendingTrim, null)?.Invoke();
    }

    private string Percent(double seconds)
    {
        // Over The Shown Window, Like The Page Script Paints
        var span = _viewEnd - _viewStart;
        var share = span > 0 ? Math.Clamp((seconds - _viewStart) / span, 0, 1) : 0;
        return $"round({Exact(share * 100)}%, var(--device-pixel))";
    }

    // A Zoomed Window's Place In The Whole Clip
    private string ZoomStyle =>
        _duration <= 0 || _viewEnd - _viewStart >= _duration - 0.001
            ? "display:none"
            : $"left:{Exact(_viewStart / _duration * 100)}%; width:{Exact((_viewEnd - _viewStart) / _duration * 100)}%";

    private string HandleStyle(double seconds)
    {
        // A Point Outside A Zoomed Window Hides
        return seconds < _viewStart - 1e-6 || seconds > _viewEnd + 1e-6
            ? $"left:{Percent(seconds)}; visibility:hidden"
            : $"left:{Percent(seconds)}";
    }

    private static string Exact(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }
    #endregion
}
