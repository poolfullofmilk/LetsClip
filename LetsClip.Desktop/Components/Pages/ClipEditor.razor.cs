using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace LetsClip.Desktop.Components.Pages;

public partial class ClipEditor : IAsyncDisposable
{
    private const double SeekStep = 5;
    private static readonly TimeSpan s_trimSaveDelay = TimeSpan.FromMilliseconds(400);

    [Parameter]
    public required string FileName { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required IJSRuntime JavaScript { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private readonly CancellationTokenSource _closing = new();
    private DotNetObjectReference<ClipEditor>? _reference;
    private ElementReference _video;
    private ElementReference _playhead;
    private ElementReference _timeline;
    private string? _path;
    private ClipInfo? _clip;
    private MediaInfo? _media;
    private ClipMetadata? _metadata;
    private string? _stripUrl;
    private bool _missing;
    private bool _ready;
    private bool _attached;
    private bool _playError;
    private bool _paused = true;
    private bool _muted;
    private double _duration;
    private double _time;
    private TrimRange _range;
    private string _startText = "";
    private string _endText = "";
    private string? _startError;
    private string? _endError;
    private Timer? _trimTimer;

    private string VideoUrl => _path is null ? "" : Shell.ClipUrl(_path);

    private string Title => _clip?.Title ?? Path.GetFileNameWithoutExtension(FileName);

    private string MuteTip => _muted ? "Unmute Preview" : "Mute Preview";

    private string PlayTip => _paused ? "Play" : "Pause";

    private static string TrimHint =>
        $"Drag the blue handles or focus one and use the arrow keys, {Formatter.FormatSeconds(TrimRange.Step)} a step, Shift for {Formatter.FormatDuration(1)}";

    // Saved Clips Open On The Asked Moment, Pre-Roll Left Outside
    private TrimRange DefaultRange =>
        _metadata is { } metadata
            ? TrimRange.Fit(metadata.KeepStart, metadata.KeepEnd, _duration)
            : TrimRange.Whole(_duration);

    protected override async Task OnParametersSetAsync()
    {
        // Only A Plain File Name In The Clips Folder
        if (
            Path.GetFileName(FileName) != FileName
            || !FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        )
        {
            _missing = true;
            return;
        }

        var path = Path.Combine(Controller.Settings.ClipsDirectory, FileName);
        var token = _closing.Token;
        _clip = await Task.Run(() => Controller.Library.GetAsync(path, token), token);
        _media = _clip is null
            ? null
            : await Task.Run(() => MediaProbe.ProbeAsync(path, token), token);
        if (_clip is null || _media is not { HasVideo: true, Duration: > 0 })
        {
            _missing = true;
            return;
        }

        _path = path;
        _metadata = ClipLibrary.MetadataOf(path);
        _duration = _media.Duration;
        _range = _metadata is { TrimStart: { } start, TrimEnd: { } end }
            ? TrimRange.Fit(start, end, _duration)
            : DefaultRange;
        _muted = Controller.IsBuffering;
        _time = _range.Start;
        ShowRange();
        _ = LoadStripAsync(path, token);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // The Element References Arrive One Render After The Markup
        if (
            _attached
            || _path is null
            || _missing
            || string.IsNullOrEmpty(_video.Id)
            || string.IsNullOrEmpty(_playhead.Id)
            || string.IsNullOrEmpty(_timeline.Id)
        )
        {
            return;
        }

        _attached = true;
        _reference = DotNetObjectReference.Create(this);
        await JavaScript.InvokeVoidAsync(
            "letsClip.attach",
            _video,
            _playhead,
            _timeline,
            _reference,
            _duration
        );
        await JavaScript.InvokeVoidAsync("letsClip.seek", _range.Start);
        _ready = true;
        StateHasChanged();
    }

    private async Task LoadStripAsync(string path, CancellationToken token)
    {
        var strip = await Task.Run(
            () => ClipLibrary.EnsureStripAsync(path, _duration, token),
            token
        );
        if (strip is not null && !token.IsCancellationRequested)
        {
            _stripUrl = DesktopShell.ThumbnailUrl(strip);
            await InvokeAsync(StateHasChanged);
        }
    }

    #region Player Calls
    [JSInvokable]
    public void OnPlayerTime(double time, bool paused)
    {
        _time = time;
        _paused = paused;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnSeek(double time)
    {
        _time = time;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnHandle(string handle, double time)
    {
        // Dragging An Edge Shows That Frame
        SetRange(handle == "start" ? _range.WithStart(time) : _range.WithEnd(time, _duration));
        await JavaScript.InvokeVoidAsync(
            "letsClip.seek",
            handle == "start" ? _range.Start : _range.End
        );
    }

    [JSInvokable]
    public void OnPlayerError()
    {
        _playError = true;
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
        ShowRange();
        StateHasChanged();
        ScheduleTrimSave();
    }

    private void ShowRange()
    {
        _startText = Formatter.FormatClock(_range.Start);
        _endText = Formatter.FormatClock(_range.End);
        _startError = null;
        _endError = null;
    }

    private void SetStartText(string text)
    {
        // A Typed Time Must Stay Before The Out Point
        if (
            Formatter.ParseClock(text) is { } start
            && start >= 0
            && start <= _range.End - TrimRange.Step
        )
        {
            SetRange(_range with { Start = start });
            ShowRange();
            return;
        }

        _startText = text;
        _startError = $"Use {Formatter.ClockPattern} before {Formatter.FormatClock(_range.End)}";
    }

    private void SetEndText(string text)
    {
        if (
            Formatter.ParseClock(text) is { } end
            && end <= _duration
            && end >= _range.Start + TrimRange.Step
        )
        {
            SetRange(_range with { End = end });
            ShowRange();
            return;
        }

        _endText = text;
        _endError = $"Use {Formatter.ClockPattern} up to {Formatter.FormatClock(_duration)}";
    }

    private void SetStartAtPlayhead()
    {
        SetRange(_range.WithStart(_time));
    }

    private void SetEndAtPlayhead()
    {
        SetRange(_range.WithEnd(_time, _duration));
    }

    private void ResetTrim()
    {
        SetRange(DefaultRange);
    }

    private void ScheduleTrimSave()
    {
        // The Selection Survives A Restart, Written Once Dragging Settles
        var path = _path;
        var range = _range;
        var isDefault = range == DefaultRange;
        _trimTimer?.Dispose();
        _trimTimer = new Timer(
            _ =>
            {
                if (path is not null)
                {
                    ClipLibrary.SaveTrim(path, isDefault ? null : range);
                }
            },
            null,
            s_trimSaveDelay,
            Timeout.InfiniteTimeSpan
        );
    }

    private string Percent(double seconds)
    {
        var share = _duration > 0 ? Math.Clamp(seconds / _duration, 0, 1) : 0;
        return (share * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    }

    private static string Number(double value)
    {
        return value.ToString("0.0", CultureInfo.InvariantCulture);
    }
    #endregion

    private async Task TogglePlayAsync()
    {
        if (_paused)
        {
            await JavaScript.InvokeVoidAsync("letsClip.play", _range.Start, _range.End);
        }
        else
        {
            await JavaScript.InvokeVoidAsync("letsClip.pause");
        }
    }

    private async Task NudgeAsync(double seconds)
    {
        await JavaScript.InvokeVoidAsync("letsClip.nudge", seconds);
    }

    private async Task ToggleMuteAsync()
    {
        _muted = !_muted;
        await JavaScript.InvokeVoidAsync("letsClip.setMuted", _muted);
        if (!_muted && Controller.IsBuffering)
        {
            Snackbar.Add("Preview sound is recorded too while buffering", Severity.Warning);
        }
    }

    private void ShowInFolder()
    {
        if (_path is not null)
        {
            Explorer.ShowFile(_path);
        }
    }

    private void Back()
    {
        NavigationManager.NavigateTo(ClipRoutes.List);
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        _closing.Dispose();
        _trimTimer?.Dispose();
        if (_attached)
        {
            try
            {
                // Playback Stops And The File Is Released On Leaving
                await JavaScript.InvokeVoidAsync("letsClip.detach");
            }
            catch (JSDisconnectedException)
            {
                // The Window Is Closing, The Browser Releases Everything Itself
            }
        }

        _reference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
