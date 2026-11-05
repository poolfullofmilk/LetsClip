using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Components.Shared.Media;
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
    public required IJSRuntime JavaScript { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    // Each Loaded Clip Has Its Own Lifetime
    private CancellationTokenSource? _lifetime;
    private DotNetObjectReference<ClipEditor>? _reference;
    private Action? _pendingTrim;
    private PlayerClock? _clock;
    private ElementReference _video;
    private ElementReference _playhead;
    private ElementReference _timeline;
    private string? _path;
    private ClipInfo? _clip;
    private MediaInfo? _media;
    private ClipMetadata? _metadata;
    private string? _stripUrl;
    private string? _loadedName;
    private bool _missing;
    private bool _ready;
    private bool _attached;
    private bool _playError;
    private bool _paused = true;
    private bool _muted;
    private double _volume = 1;
    private double _duration;
    private double _time;
    private TrimRange _range;
    private Timer? _trimTimer;

    private string VideoUrl => _path is null ? "" : DesktopShell.ClipUrl(Path.GetFileName(_path));

    // A Generated Name Shows As Its Date
    private string Title =>
        ClipNames.DateOf(Path.GetFileNameWithoutExtension(FileName)) is { } date
            ? Formatter.FormatDateTime(date)
            : Path.GetFileNameWithoutExtension(FileName);

    private string MuteTip => _muted ? "Unmute Preview" : "Mute Preview";

    private string PlayTip => _paused ? "Play" : "Pause";

    private TrimRange DefaultRange => TrimRange.Whole(_duration);

    protected override async Task OnParametersSetAsync()
    {
        // The Tray Balloon Can Open Another Clip Here
        if (FileName == _loadedName)
        {
            return;
        }

        _loadedName = FileName;
        EndClip();
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        var token = lifetime.Token;
        (_clip, _media, _path, _metadata, _stripUrl) = (null, null, null, null, null);
        (_missing, _ready, _playError, _paused) = (false, false, false, true);
        await DetachAsync();

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
        var clip = await Task.Run(() => Controller.Library.GetAsync(path, token), token);
        var media = clip is null
            ? null
            : await Task.Run(() => MediaProbe.ProbeAsync(path, token), token);

        // Another Clip Opened Meanwhile, This Result Belongs To Nobody
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (clip is null || media is not { HasVideo: true, Duration: > 0 })
        {
            _missing = true;
            return;
        }

        // Window Clips Keep Their Capture Rate, Probes Read Low
        _clip = clip;
        _path = path;
        _metadata = ClipLibrary.MetadataOf(path);
        _media = media with { FrameRate = _metadata?.FrameRate ?? media.FrameRate };
        _duration = _media.Duration;
        _range = _metadata is { TrimStart: { } start, TrimEnd: { } end }
            ? TrimRange.Fit(start, end, _duration)
            : DefaultRange;
        _muted = Controller.IsBuffering;
        _time = _range.Start;
        _ = LoadStripAsync(path, _duration, token);
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

        // A Reference Of This Clip's Own, Let Go On Detach
        _attached = true;
        _reference = DotNetObjectReference.Create(this);
        await JavaScript.InvokeVoidAsync(
            "letsClip.attach",
            _video,
            _playhead,
            _timeline,
            _reference,
            _duration,
            _range.Start
        );
    }

    private async Task DetachAsync()
    {
        if (!_attached)
        {
            return;
        }

        // Playback Stops And The File Is Released
        _attached = false;
        var reference = _reference;
        _reference = null;
        try
        {
            await JavaScript.InvokeVoidAsync("letsClip.detach");
        }
        catch (JSDisconnectedException)
        {
            // The Window Is Closing, The Browser Releases Everything Itself
        }
        finally
        {
            reference?.Dispose();
        }
    }

    private void EndClip()
    {
        // Its Loads Stop And Its Pending Trim Is Written
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        SaveTrimNow();
    }

    private async Task LoadStripAsync(string path, double duration, CancellationToken token)
    {
        var strip = await Task.Run(
            () => ClipLibrary.EnsureStripAsync(path, duration, token),
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
        // Only The Clock Follows Playback, Not The Whole Editor
        _time = time;
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
        _ready = true;
        StateHasChanged();
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
        StateHasChanged();
        ScheduleTrimSave();
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
        if (_path is not { } path)
        {
            return;
        }

        var range = _range == DefaultRange ? (TrimRange?)null : _range;
        Volatile.Write(ref _pendingTrim, () => ClipLibrary.SaveTrim(path, range));
        _trimTimer ??= new Timer(
            _ => SaveTrimNow(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _trimTimer.Change(s_trimSaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void SaveTrimNow()
    {
        // Taken Once, So The Trim Is Never Written Twice
        Interlocked.Exchange(ref _pendingTrim, null)?.Invoke();
    }

    private string Percent(double seconds)
    {
        var share = _duration > 0 ? Math.Clamp(seconds / _duration, 0, 1) : 0;
        return (share * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    }

    private static string Exact(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
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
        if (!_muted && _volume == 0)
        {
            await SetVolumeAsync(1);
        }

        if (!_muted && Controller.IsBuffering)
        {
            Snackbar.Add("Preview sound is recorded too while buffering", Severity.Warning);
        }
    }

    private async Task SetVolumeAsync(double volume)
    {
        // Sliding To Zero Mutes, Sliding Up Unmutes
        _volume = volume;
        await JavaScript.InvokeVoidAsync("letsClip.setVolume", volume);
        if (_muted == volume > 0)
        {
            await ToggleMuteAsync();
        }
    }

    private async Task FullScreenAsync()
    {
        await JavaScript.InvokeVoidAsync("letsClip.fullScreen");
    }

    private async Task DeleteAsync()
    {
        if (
            _path is not { } path
            || !await DialogService.ConfirmAsync(Clips.DeleteTitle, Clips.DeleteText)
        )
        {
            return;
        }

        // The Preview Lets Go Of The File First
        await DetachAsync();
        if (!Explorer.Recycle(path))
        {
            Snackbar.Add("The clip could not be deleted, it may be in use", Severity.Warning);
        }

        Back();
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
        // Writes A Pending Trim, Then Lets Go Of The File
        _trimTimer?.Dispose();
        EndClip();
        await DetachAsync();
        GC.SuppressFinalize(this);
    }
}
