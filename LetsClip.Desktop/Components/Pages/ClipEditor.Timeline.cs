using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
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
        // Dragged Past The Clip's End, What Came After Joins It
        if (end > _duration + (TrimRange.Step / 2) && _after > 0 && !Exporting)
        {
            _ = ExtendToAsync(start, Math.Min(end, Total));
            return;
        }

        // The Page Script Dragged The Handles, Here The Range Settles
        SetRange(TrimRange.Fit(start, end, _duration));
        StateHasChanged();
        _panel?.ShowLive(null, null);
    }

    private async Task ExtendToAsync(double start, double end)
    {
        if (_path is not { } path || _extension is not { } extension)
        {
            return;
        }

        // Saved Again Longer, The Editor Reloads Without A Flash
        _panel?.ShowLive(new TrimRange(start, end), null);
        await ReplaceFileAsync(async () =>
        {
            await ReplayRecorder.ExtendAsync(extension, path, end, start);
            return path;
        });
    }

    [JSInvokable]
    public void OnRanging(double start, double end)
    {
        // Mid Drag The Export's Numbers Follow, Nothing Is Saved
        _panel?.ShowLive(TrimRange.Fit(start, end, Total), null);
    }

    [JSInvokable]
    public void OnPlayerReady()
    {
        // Controls Wait For The First Frame
        (_ready, _placeCrop) = (true, true);
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnView(double start, double end)
    {
        // Settled After The Wheel, Blazor Draws The Same Window
        (_viewStart, _viewEnd) = (start, end);
        StateHasChanged();
        if (_path is not { } path || end - start >= Total - 0.001 || start >= _duration - 1)
        {
            return;
        }

        // A Zoomed Window Gets Its Own Strip, The Newest Wins
        var zoom = ++_zooms;
        var (duration, token) = (_duration, _lifetime?.Token ?? CancellationToken.None);
        var window = new TrimRange(start, Math.Min(end, _duration));
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
                        () => ClipLibrary.EnsureWaveAsync(path, duration, token, window),
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
                    window.Start,
                    window.End
                );
            }
        }
        catch (OperationCanceledException)
        {
            // The Stretched Whole Clip Strip Stays
        }
    }

    [JSInvokable]
    public void OnCrop(double left, double top, double width, double height)
    {
        // Moved Or Sized In The Page, Remembered With The Clip
        if (_path is not { } path || _media is not { } media)
        {
            return;
        }

        // Over The Whole Frame, Nothing Is Cropped
        var box = new CropBox(left, top, width, height).Clamped();
        var crop = box.Changes(media.Width, media.Height) ? box : null;
        _crop = crop;
        _ = Task.Run(() => ClipLibrary.UpdateMetadata(path, metadata => metadata.WithBox(crop)));
        StateHasChanged();
        _panel?.ShowLive(null, null);
    }

    [JSInvokable]
    public void OnCropping(double left, double top, double width, double height)
    {
        // Mid Drag Only The Export's Numbers Follow
        _panel?.ShowLive(null, new CropBox(left, top, width, height).Clamped());
    }

    private void OnStreamsChanged()
    {
        // A Crop Box Shown Again Is Placed Again
        _placeCrop = true;
    }

    private static string CropStyle(CropBox crop)
    {
        return $"--crop-left:{Exact(crop.Left)}; --crop-top:{Exact(crop.Top)}; --crop-width:{Exact(crop.Width)}; --crop-height:{Exact(crop.Height)}";
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
        Total <= 0 || _viewEnd - _viewStart >= Total - 0.001
            ? "display:none"
            : $"left:{Exact(_viewStart / Total * 100)}%; width:{Exact((_viewEnd - _viewStart) / Total * 100)}%";

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
