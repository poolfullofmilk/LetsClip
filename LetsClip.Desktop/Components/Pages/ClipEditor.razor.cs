using System.ComponentModel;
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
    private PlayerClock? _clock;
    private ElementReference _video;
    private ElementReference _playhead;
    private ElementReference _timeline;
    private ElementReference _strip;
    private string? _drawnStrip;
    private string? _waveUrl;
    private string? _drawnWave;

    private static int StripCells => ClipLibrary.StripCells;
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

    // A Vertical Export's Crop, Placed Once Shown
    private double? _crop;
    private bool _placeCrop;

    // The Timeline's Shown Window, The Whole Clip Unless Zoomed
    private double _viewStart;
    private double _viewEnd;
    private int _zooms;
    private Timer? _saveTimer;
    private MudTextField<string>? _titleField;
    private string _titleText = "";

    // The File's Name, Typing Changes Only The Shown One
    private string? _savedTitle;

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

    // Its Export Reads The File, Nothing May Replace It
    private bool Exporting => Controller.Exports.IsExporting(_path);

    // A Cut Or Restore Swapping The File Locks Every Edit
    private bool Editable => _ready && !_swapping;

    // The Crop Box Shows While The Export Keeps A Picture
    private bool ShowsCrop =>
        _ready
        && _media is { } media
        && ClipExporter.KeepsVideo(
            media,
            Controller.Settings.ExportVideo,
            Controller.Settings.ExportAudio,
            Controller.Settings.ExportMicrophone
        );

    // A Clip Without Sound Has No Volume To Change
    private bool Audible => Editable && _media?.HasAudio == true;

    private string PlayTip => _paused ? "Play" : "Pause";

    protected override void OnInitialized()
    {
        Controller.Exports.Changed += OnExportsChanged;
    }

    private void OnExportsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

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

        // Another Clip Opening Here Leaves This One Named
        if (opening)
        {
            RenameLeftClip();
        }

        // Only A Plain File Name In The Clips Folder
        if (
            _loadedName is not { } fileName
            || Path.GetFileName(fileName) != fileName
            || !fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        )
        {
            ShowMissing();
            return;
        }

        var path = Path.Combine(Controller.Settings.ClipsDirectory, fileName);
        // The Streams, Then Every Packet If Its Listing Ends
        var clip = await Task.Run(() => Controller.Library.GetAsync(path, token), token);
        MediaInfo? media = null;
        try
        {
            media = clip is null
                ? null
                : await Task.Run(
                    () => MediaProbe.ProbeAsync(path, token, withPackets: true),
                    token
                );
        }
        catch (Exception exception) when (exception is TimeoutException or Win32Exception)
        {
            // An Unreadable Clip Says So, The Page Never Throws
            s_logger.Warn($"Failed To Open {fileName} {exception.GetType().Name}");
            Snackbar.Add("The clip could not be read, it may be in use", Severity.Warning);
            Back();
            return;
        }

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
            metadata?.Crop,
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
        // One Step, So Nothing On Screen Changes Piecemeal
        (_clip, _media, _path) = (loaded.Clip, loaded.Media, loaded.Path);
        Controller.Exports.Editor = (loaded.Path, CutHereAsync);
        (_stripUrl, _duration, _range) = (loaded.StripUrl, loaded.Media.Duration, loaded.Range);
        (_viewStart, _viewEnd) = (0, loaded.Media.Duration);
        (_crop, _placeCrop) = (loaded.Crop, loaded.Crop is not null);
        _waveUrl = null;
        if (loaded.Media.HasAudio)
        {
            _ = LoadWaveAsync(loaded.Path, loaded.Media.Duration, loaded.Token);
        }
        _savedTitle = loaded.Clip.CustomTitle;
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
            $"Opened {_loadedName} In The Editor, {Formatter.FormatDuration(_duration)} At {loaded.Media.Width} × {loaded.Media.Height}, {loaded.Media.FrameRate:0} FPS, Kept {Formatter.FormatDuration(_range.Start)} To {Formatter.FormatDuration(_range.End)}"
        );
    }

    private void ShowMissing()
    {
        (_clip, _media, _path, _stripUrl) = (null, null, null, null);
        (_missing, _swapping) = (true, false);
        LetGoOfCuts();
    }

    private void LetGoOfCuts()
    {
        // Exports Ending Later Cut The File Directly
        if (Controller.Exports.Editor?.Cut.Target == this)
        {
            Controller.Exports.Editor = null;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // A New Strip Is Drawn To Fit, Whole Frames Only
        if (_stripUrl != _drawnStrip && !string.IsNullOrEmpty(_strip.Id))
        {
            (_drawnStrip, _drawnWave) = (_stripUrl, null);
            await JavaScript.CallAsync("letsClip.showStrip", _strip);
        }

        // The Sound's Outline Joins The Strip Whenever It Is Ready
        if (_waveUrl != _drawnWave && _drawnStrip is not null && !string.IsNullOrEmpty(_strip.Id))
        {
            _drawnWave = _waveUrl;
            await JavaScript.CallAsync("letsClip.showWave", _strip, _waveUrl);
        }

        // The Crop Box Lies Over The Picture Once Drawn
        if (_placeCrop && _ready)
        {
            _placeCrop = false;
            await JavaScript.CallAsync("letsClip.placeCrop");
        }

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

    private async Task LoadWaveAsync(string path, double duration, CancellationToken token)
    {
        // A Missing Outline Leaves The Plain Strip
        var wave = await Task.Run(() => ClipLibrary.EnsureWaveAsync(path, duration, token), token);
        if (!token.IsCancellationRequested)
        {
            _waveUrl = wave is null ? null : DesktopShell.ThumbnailUrl(wave);
            await InvokeAsync(StateHasChanged);
        }
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
        // Shown At Once, The File Takes It When Used
        _titleText = text;
        if (_clip is { } clip)
        {
            _clip = clip with { CustomTitle = clip.CustomTitleFrom(text) };
        }
    }

    private void LeaveTitle()
    {
        // Trimmed Like It Will Be Kept, Nothing Locks Meanwhile
        _titleText = _clip?.CustomTitle ?? "";
    }

    private bool IsRenaming => _clip is { } clip && clip.CustomTitle != _savedTitle;

    private async Task<string?> SettledPathAsync()
    {
        // A Typed Name Reaches The File Before Anything Uses It
        if (IsRenaming && _clip is { } clip && _path is { } path)
        {
            await ReplaceFileAsync(() => ClipLibrary.RenameAsync(path, clip.CustomTitle));

            // The Renamed Clip Shows In Its View Transition, Then Acts
            for (var waited = 0; _swapping && waited < 50; waited++)
            {
                await Task.Delay(100);
            }
        }

        return _swapping ? null : _path;
    }

    private void RenameLeftClip()
    {
        // A Name Typed Here Lands Once The Clip Is Left
        if (IsRenaming && _clip is { } clip && _path is { } path)
        {
            var exports = Controller.Exports;
            _ = Task.Run(() => RenameAfterLeavingAsync(exports, path, clip.CustomTitle));
        }
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
    private Task<bool> CutHereAsync(TrimRange range)
    {
        // An Export That Ended Here Cuts Through The Player
        return _path is { } path ? SaveCutAsync(path, range) : Task.FromResult(false);
    }

    private async Task<bool> SaveCutAsync(string path, TrimRange range)
    {
        // Cut To The Kept Part, The Original Kept If Asked
        if (path != _path)
        {
            return false;
        }

        var keepOriginal = !Controller.Settings.DeleteOriginals;
        _cutting = true;
        StateHasChanged();
        var cut = await ReplaceFileAsync(async () =>
        {
            await ClipLibrary.CutAsync(path, range, keepOriginal, CancellationToken.None);
            return path;
        });
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

        return cut;
    }

    private bool CanSaveCut()
    {
        // Only A Shorter Selection Has Anything To Cut
        return Editable && !Exporting && _range.CutsShort(_duration);
    }

    private async Task RestoreOriginalAsync()
    {
        if (await SettledPathAsync() is not { } path)
        {
            return;
        }

        _restoring = true;
        var restored = await ReplaceFileAsync(async () =>
        {
            await ClipLibrary.RestoreAsync(path);
            return path;
        });
        _restoring = false;
        if (restored)
        {
            Snackbar.Add("Restored the whole clip", Severity.Success);
        }
    }

    private async Task<bool> ReplaceFileAsync(Func<Task<string>> replace)
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
            var replacedPath = await Task.Run(replace);
            replaced = true;
            if (_path is { } changed)
            {
                Clips.Replaced(changed);
            }

            // A Rename Opens The Clip Under Its New Name
            if (Path.GetFileName(replacedPath) is var name && name != _loadedName)
            {
                if (_path is { } renamed)
                {
                    Controller.Exports.Renamed(renamed, replacedPath);
                }

                _loadedName = name;
                NavigationManager.NavigateTo(Clips.EditorOf(replacedPath), replace: true);
            }
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or TimeoutException
            )
        {
            s_logger.Warn($"Failed To Replace {_loadedName} {exception.GetType().Name}");
            Snackbar.Add("The clip could not be changed, it may be in use", Severity.Warning);
        }

        await LoadAsync(opening: false);
        return replaced;
    }
    #endregion

    private async Task DeleteAsync()
    {
        if (_path is not { } path)
        {
            return;
        }

        // The Preview Lets Go First, A Typed Name Goes Too
        await DetachAsync();
        Clips.DeleteLater(path, Snackbar);
        _path = null;
        Back();
    }

    private async Task ShowInFolderAsync()
    {
        if (await SettledPathAsync() is { } path)
        {
            Explorer.ShowFile(path);
        }
    }

    private async Task CopyFileAsync()
    {
        // The Whole File, Selection Or Not, Under Its Typed Name
        if (await SettledPathAsync() is { } path)
        {
            DesktopShell.CopyFile(path, Snackbar);
        }
    }

    private void ShowSourceClips()
    {
        if (_clip is { } clip)
        {
            Clips.ShowOnly(clip.Source);
            Back();
        }
    }

    private void Back()
    {
        NavigationManager.NavigateTo(Clips.Route);
    }

    private static async Task RenameAfterLeavingAsync(
        ExportRunner exports,
        string path,
        string? title
    )
    {
        try
        {
            exports.Renamed(path, await ClipLibrary.RenameAsync(path, title));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Kept Under Its Old Name, Nothing Is Lost
            s_logger.Warn($"Failed To Rename A Left Clip {exception.GetType().Name}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Writes A Pending Trim, Then Lets Go Of The File
        Controller.Exports.Changed -= OnExportsChanged;
        LetGoOfCuts();
        _saveTimer?.Dispose();
        EndClip();
        await DetachAsync();
        RenameLeftClip();
        _page?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record LoadedClip(
        ClipInfo Clip,
        MediaInfo Media,
        string Path,
        string? StripUrl,
        TrimRange Range,
        double? Crop,
        bool Opening,
        CancellationToken Token
    );
}
