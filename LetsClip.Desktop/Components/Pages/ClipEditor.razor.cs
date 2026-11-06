using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Common;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Components.Shared.Media;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using NLog;

namespace LetsClip.Desktop.Components.Pages;

public partial class ClipEditor : IAsyncDisposable
{
    private const double SeekStep = 5;

    // Every Clip Opens Playing At Half Volume, Changed In Tenths
    private const double OpeningVolume = 0.5;

    // Writes Wait For Dragging Or Typing To Settle
    private static readonly TimeSpan s_saveDelay = TimeSpan.FromMilliseconds(400);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

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
    private Action? _pendingTitle;
    private PlayerClock? _clock;
    private ElementReference _video;
    private ElementReference _playhead;
    private ElementReference _timeline;
    private string? _path;
    private ClipInfo? _clip;
    private MediaInfo? _media;
    private string? _stripUrl;
    private double[] _frames = [];
    private string? _loadedName;
    private bool _missing;
    private bool _ready;
    private bool _attached;
    private bool _playError;
    private bool _paused = true;
    private bool _muted;
    private double _volume = OpeningVolume;
    private int _volumeShown;
    private double _duration;
    private TrimRange _range;
    private Timer? _saveTimer;
    private MudTextField<string>? _titleField;
    private string _titleText = "";

    private string VideoUrl => _path is null ? "" : DesktopShell.ClipUrl(_path);

    private string Title => _clip?.Title ?? "";

    // The Game Or App, Shown While The Field Is Empty
    private string DefaultTitle =>
        _clip is { } clip ? (clip with { CustomTitle = null }).Title : "";

    private string RecordedText =>
        _clip is { } clip ? Formatter.FormatDateTime(clip.RecordedAt) : "";

    private string MuteTip => _muted ? "Unmute Preview" : "Mute Preview";

    private string VolumeText => _muted ? "Muted" : $"{_volume * 100:0}%";

    // The Side Panel Gives The Player Just The Clip's Width
    private double ClipAspect =>
        _media is { Width: > 0, Height: > 0 } media ? media.Width / (double)media.Height : 16 / 9.0;

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
        (_clip, _media, _path, _stripUrl) = (null, null, null, null);
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
        var frames = Task.Run(() => MediaProbe.FrameTimesAsync(path, token), token);
        var media = clip is null
            ? null
            : await Task.Run(() => MediaProbe.ProbeAsync(path, token), token);
        _frames = await frames;

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
        _titleText = clip.Title;
        _path = path;
        var metadata = ClipLibrary.MetadataOf(path);
        _media = media with { FrameRate = metadata?.FrameRate ?? media.FrameRate };
        _duration = _media.Duration;
        _range = metadata is { TrimStart: { } start, TrimEnd: { } end }
            ? TrimRange.Fit(start, end, _duration)
            : DefaultRange;
        (_muted, _volume, _volumeShown) = (false, OpeningVolume, 0);
        s_logger.Info(
            $"Opened {FileName} In The Editor, {Formatter.FormatDuration(_duration)} At {_media.Width} × {_media.Height}, {_media.FrameRate:0} FPS, Kept {Formatter.FormatDuration(_range.Start)} To {Formatter.FormatDuration(_range.End)}"
        );
        _ = LoadStripAsync(path, _duration, token);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Element References Arrive One Render After The Markup
        if (_attached || _path is null || string.IsNullOrEmpty(_video.Id))
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
            _range.Start,
            _range.End,
            _volume,
            CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator,
            _frames
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
        SaveNow();
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

    private async Task MarkAsync(string edge)
    {
        // The Page Script Knows The Frame On Screen
        await JavaScript.InvokeVoidAsync("letsClip.mark", edge);
    }

    private async Task PlaceAsync(string edge, double seconds)
    {
        // A Typed Point Snaps To Its Frame Like A Drag
        await JavaScript.InvokeVoidAsync("letsClip.place", edge, seconds);
    }

    private void ScheduleTrimSave()
    {
        // The Selection Survives A Restart, Written Once Dragging Settles
        if (_path is not { } path)
        {
            return;
        }

        var range = _range == DefaultRange ? (TrimRange?)null : _range;
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
        Interlocked.Exchange(ref _pendingTitle, null)?.Invoke();
    }

    private string Percent(double seconds)
    {
        var share = _duration > 0 ? Math.Clamp(seconds / _duration, 0, 1) : 0;
        return Exact(share * 100) + "%";
    }

    private static string Exact(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
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
        await ApplyVolumeAsync(!_muted, _volume);
    }

    private async Task SetVolumeAsync(double level)
    {
        // Zero Mutes, The Last Level Stays For Unmuting
        await ApplyVolumeAsync(level <= 0, level > 0 ? level : _volume);
    }

    private async Task ApplyVolumeAsync(bool muted, double volume)
    {
        // Each Change Shows Its Level Over The Clip
        (_muted, _volume) = (muted, volume);
        _volumeShown++;
        await JavaScript.InvokeVoidAsync("letsClip.setVolume", volume, muted);
    }

    private async Task FullScreenAsync()
    {
        await JavaScript.InvokeVoidAsync("letsClip.fullScreen");
    }

    #region Title
    private void TypeTitle(string text)
    {
        _titleText = text;
        if (_clip is not { } clip || _path is not { } path)
        {
            return;
        }

        // Empty Or The Default Name Means The Default
        var typed = text.Trim();
        var title = typed.Length == 0 || typed == DefaultTitle ? null : typed;
        if (title == clip.CustomTitle)
        {
            return;
        }

        _clip = clip with { CustomTitle = title };
        var fileName = FileName;
        Volatile.Write(
            ref _pendingTitle,
            () =>
            {
                ClipLibrary.UpdateMetadata(path, metadata => metadata with { Title = title });
                s_logger.Info($"Renamed {fileName} To {title ?? "Its Default Name"}");
            }
        );
        SaveSoon();
    }

    private void SettleTitle()
    {
        // Leaving Writes At Once, An Empty Field Shows The Default
        SaveNow();
        _titleText = Title;
    }

    private async Task TitleKeyAsync(KeyboardEventArgs key)
    {
        // Enter Or Escape Ends Typing Like Leaving The Field
        if (key.Key is "Enter" or "Escape" && _titleField is { } field)
        {
            await field.BlurAsync();
        }
    }
    #endregion

    #region Cut And Restore
    private async Task SaveCutAsync(TrimRange range)
    {
        // The Clip Becomes The Kept Part, Its Original Stays Hidden
        if (_path is not { } path)
        {
            return;
        }

        await ReplaceFileAsync(() => ClipLibrary.CutAsync(path, range, CancellationToken.None));
        Snackbar.Add(
            "Saved the cut, Restore Original brings back the whole clip",
            Severity.Success
        );
    }

    private async Task RestoreOriginalAsync()
    {
        if (_path is { } path)
        {
            await ReplaceFileAsync(() => ClipLibrary.RestoreAsync(path));
        }
    }

    private async Task ReplaceFileAsync(Func<Task> replace)
    {
        // Pending Writes Land First, Then The Player Lets Go
        SaveNow();
        await DetachAsync();
        try
        {
            await Task.Run(replace);
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or TimeoutException
            )
        {
            s_logger.Warn($"Failed To Replace {FileName} {exception.GetType().Name}");
            Snackbar.Add("The clip could not be changed, it may be in use", Severity.Warning);
        }

        _loadedName = null;
        await OnParametersSetAsync();
        StateHasChanged();
    }
    #endregion

    private async Task DeleteAsync()
    {
        if (_path is not { } path || !await DialogService.ConfirmDeleteAsync())
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
        _saveTimer?.Dispose();
        EndClip();
        await DetachAsync();
        GC.SuppressFinalize(this);
    }
}
