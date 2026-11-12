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
    private string? _loadedName;
    private bool _missing;
    private bool _ready;
    private bool _attached;

    // A Clip Or File Is Changing Behind The Held Frame
    private bool _swapping;
    private bool _cutting;
    private bool _restoring;
    private bool _stripLoading;
    private bool _exporting;
    private LoadedClip? _loaded;
    private DotNetObjectReference<ClipEditor>? _page;
    private bool _playError;
    private bool _paused = true;
    private bool _autoPlay;
    private bool _muted;
    private double _volume;
    private int _volumeShown;
    private double _duration;
    private TrimRange _range;
    private Timer? _saveTimer;
    private MudTextField<string>? _titleField;
    private string _titleText = "";

    // No Disk Read, The Loaded Clip Knows Its Write Time
    private string VideoUrl => _clip is null ? "" : DesktopShell.ClipUrl(_clip);

    private string Title => _clip?.Title ?? "";

    private string RecordedText =>
        _clip is { } clip ? Formatter.FormatDateTime(clip.RecordedAt) : "";

    private string MuteTip => _muted ? "Unmute Preview" : "Mute Preview";

    private string VolumeText => _muted ? "Muted" : $"{_volume * 100:0}%";

    // The Side Panel Gives The Player Just The Clip's Width
    private double ClipAspect =>
        _media is { Width: > 0, Height: > 0 } media ? media.Width / (double)media.Height : 16 / 9.0;

    // A Cut Or Restore Swapping The File Locks Every Edit
    private bool Editable => _ready && !_swapping;

    private string PlayTip => _paused ? "Play" : "Pause";

    protected override async Task OnParametersSetAsync()
    {
        // The Tray Balloon Can Open Another Clip Here
        if (FileName != _loadedName)
        {
            _loadedName = FileName;
            await LoadAsync(opening: true);
        }
    }

    private async Task LoadAsync(bool opening)
    {
        // The Clip On Screen Stays Until The Next Is Ready
        var seamless = _clip is not null;
        _swapping = seamless;
        _autoPlay = opening;
        EndClip();
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        var token = lifetime.Token;
        if (!seamless)
        {
            (_missing, _ready, _playError, _paused) = (false, false, false, true);
        }

        await DetachAsync(seamless);

        // Only A Plain File Name In The Clips Folder
        if (
            Path.GetFileName(FileName) != FileName
            || !FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        )
        {
            ShowMissing();
            return;
        }

        var path = Path.Combine(Controller.Settings.ClipsDirectory, FileName);
        // One Probe Reads The Streams And Every Packet
        var clip = await Task.Run(() => Controller.Library.GetAsync(path, token), token);
        var media = clip is null
            ? null
            : await Task.Run(() => MediaProbe.ProbeAsync(path, token, withPackets: true), token);

        // Another Clip Opened Meanwhile, This Result Belongs To Nobody
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (clip is null || media is not { HasVideo: true, Duration: > 0 })
        {
            ShowMissing();
            return;
        }

        // Its Timeline Is Ready Before Anything Changes On Screen
        var strip = seamless
            ? await Task.Run(() => ClipLibrary.EnsureStripAsync(path, media.Duration, token), token)
            : null;

        // Window Clips Keep Their Capture Rate, Probes Read Low
        var metadata = ClipLibrary.MetadataOf(path);
        var loaded = new LoadedClip(
            clip,
            media with
            {
                FrameRate = metadata?.FrameRate ?? media.FrameRate,
            },
            path,
            strip is null ? null : DesktopShell.ThumbnailUrl(strip),
            metadata is { TrimStart: { } start, TrimEnd: { } end }
                ? TrimRange.Fit(start, end, media.Duration)
                : TrimRange.Whole(media.Duration),
            opening,
            token
        );
        if (!seamless)
        {
            Show(loaded);
            _stripLoading = true;
            _ = LoadStripAsync(path, media.Duration, token);
            return;
        }

        _loaded = loaded;
        _page ??= DotNetObjectReference.Create(this);
        await JavaScript.CallAsync("letsClip.transition", _page, nameof(ShowLoaded));
    }

    [JSInvokable]
    public void ShowLoaded()
    {
        // A Newer Clip Wins, This One Is Dropped
        if (
            Interlocked.Exchange(ref _loaded, null) is { } loaded
            && !loaded.Token.IsCancellationRequested
        )
        {
            Show(loaded);
            StateHasChanged();
        }
    }

    private void Show(LoadedClip loaded)
    {
        // Another Clip Has No Export Of Its Own Running
        _exporting &= loaded.Path == _path;

        // One Step, So Nothing On Screen Changes Piecemeal
        (_clip, _media, _path) = (loaded.Clip, loaded.Media, loaded.Path);
        (_stripUrl, _duration, _range) = (loaded.StripUrl, loaded.Media.Duration, loaded.Range);
        (_titleText, _swapping, _playError, _paused) = (
            loaded.Clip.CustomTitle ?? "",
            false,
            false,
            true
        );

        // Opens At The Last Level, A Cut Keeps Its Own
        if (loaded.Opening)
        {
            var settings = Controller.Settings;
            (_muted, _volume, _volumeShown) = (settings.PlayerMuted, settings.PlayerVolume, 0);
        }

        s_logger.Info(
            $"Opened {FileName} In The Editor, {Formatter.FormatDuration(_duration)} At {loaded.Media.Width} × {loaded.Media.Height}, {loaded.Media.FrameRate:0} FPS, Kept {Formatter.FormatDuration(_range.Start)} To {Formatter.FormatDuration(_range.End)}"
        );
    }

    private void ShowMissing()
    {
        (_clip, _media, _path, _stripUrl) = (null, null, null, null);
        (_missing, _swapping) = (true, false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Element References Arrive One Render After The Markup
        if (_attached || _swapping || _path is null || string.IsNullOrEmpty(_video.Id))
        {
            return;
        }

        // A Reference Of This Clip's Own, Let Go On Detach
        _attached = true;
        _reference = DotNetObjectReference.Create(this);
        await JavaScript.CallAsync(
            "letsClip.attach",
            _video,
            VideoUrl,
            _playhead,
            _timeline,
            _reference,
            _duration,
            _range.Start,
            _range.End,
            _volume,
            _muted,
            CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator,
            _media?.Packets?.Times ?? [],
            _media?.Packets?.Sizes ?? [],
            _media?.Packets?.Keys ?? [],
            _autoPlay
        );
    }

    private async Task DetachAsync(bool holdFrame = false)
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
            await JavaScript.CallAsync("letsClip.detach", holdFrame);
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
        if (!token.IsCancellationRequested)
        {
            // A Strip That Failed Stops Its Spinner Too
            _stripLoading = false;
            _stripUrl = strip is null ? null : DesktopShell.ThumbnailUrl(strip);
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
        Interlocked.Exchange(ref _pendingTitle, null)?.Invoke();
    }

    private string Percent(double seconds)
    {
        var share = _duration > 0 ? Math.Clamp(seconds / _duration, 0, 1) : 0;
        return $"round({Exact(share * 100)}%, var(--device-pixel))";
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
            await JavaScript.CallAsync("letsClip.play", _range.Start, _range.End);
        }
        else
        {
            await JavaScript.CallAsync("letsClip.pause");
        }
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

        // The Next Clip Opens At This Level
        Controller.UpdateSettings(settings =>
            settings with
            {
                PlayerMuted = muted,
                PlayerVolume = volume,
            }
        );
        await JavaScript.CallAsync("letsClip.setVolume", volume, muted);
    }

    private async Task FullScreenAsync()
    {
        await JavaScript.CallAsync("letsClip.fullScreen");
    }

    #region Title
    private void TypeTitle(string text)
    {
        _titleText = text;
        if (_clip is not { } clip || _path is not { } path)
        {
            return;
        }

        // Empty Or The Shown Default Stores No Name
        var typed = text.Trim();
        var title = typed.Length == 0 || typed == clip.DefaultTitle ? null : typed;
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
        // Leaving Writes At Once, Trimmed Like The Saved Name
        SaveNow();
        _titleText = _clip?.CustomTitle ?? "";
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
    private void SetExporting(bool exporting)
    {
        _exporting = exporting;
    }

    private async Task SaveCutAsync(string path, TrimRange range)
    {
        // Cut To The Kept Part, The Original Kept If Asked
        if (path != _path)
        {
            return;
        }

        var keepOriginal = !Controller.Settings.DeleteOriginals;
        _cutting = true;
        StateHasChanged();
        var cut = await ReplaceFileAsync(() =>
            ClipLibrary.CutAsync(path, range, keepOriginal, CancellationToken.None)
        );
        _cutting = false;
        StateHasChanged();
        if (cut)
        {
            Snackbar.Add(
                keepOriginal
                    ? "Saved the cut, Restore Original brings back the whole clip"
                    : "Saved the cut",
                Severity.Success
            );
        }
    }

    private bool CanSaveCut()
    {
        // Only A Shorter Selection Has Anything To Cut
        return Editable && !_exporting && _range.CutsShort(_duration);
    }

    private async Task RestoreOriginalAsync()
    {
        if (_path is not { } path)
        {
            return;
        }

        _restoring = true;
        var restored = await ReplaceFileAsync(() => ClipLibrary.RestoreAsync(path));
        _restoring = false;
        if (restored)
        {
            Snackbar.Add("Restored the whole clip", Severity.Success);
        }
    }

    private async Task<bool> ReplaceFileAsync(Func<Task> replace)
    {
        // One At A Time, Nothing May Reattach Meanwhile
        if (_swapping)
        {
            return false;
        }

        // Pending Writes Land First, Then The Player Lets Go
        (_swapping, _paused) = (true, true);
        SaveNow();
        await DetachAsync(holdFrame: true);
        var replaced = false;
        try
        {
            await Task.Run(replace);
            replaced = true;
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

        await LoadAsync(opening: false);
        return replaced;
    }
    #endregion

    private async Task DeleteAsync()
    {
        if (_path is not { } path || !await YesNoDialog.ConfirmDeleteAsync(DialogService))
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

    private void CopyFile()
    {
        // The Whole File, Selection Or Not
        if (_path is not null)
        {
            DesktopShell.CopyFile(_path, Snackbar);
        }
    }

    private void Back()
    {
        NavigationManager.NavigateTo(Clips.Route);
    }

    public async ValueTask DisposeAsync()
    {
        // Writes A Pending Trim, Then Lets Go Of The File
        _saveTimer?.Dispose();
        EndClip();
        await DetachAsync();
        _page?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record LoadedClip(
        ClipInfo Clip,
        MediaInfo Media,
        string Path,
        string? StripUrl,
        TrimRange Range,
        bool Opening,
        CancellationToken Token
    );
}
