using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Common;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using NLog;

namespace LetsClip.Desktop.Components.Pages;

public partial class Settings : IDisposable
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // The Storage Panel, Where Kept Originals Are Set
    public const string StorageRoute = "settings#storage";

    // A Slid Volume Plays Once Settled, Restarts Cut The Chime
    private static readonly TimeSpan s_chimeDelay = TimeSpan.FromMilliseconds(300);

    // Renewed Each Second, So A Restarted Session Is Muted Too
    private static readonly TimeSpan s_loopbackMute = TimeSpan.FromSeconds(2);

    // One Plug Fires Several Events, One Refresh Follows
    private static readonly TimeSpan s_deviceSettle = TimeSpan.FromMilliseconds(400);

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required IJSRuntime JavaScript { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    [Inject]
    public required NavigationManager Navigation { get; set; }

    // Null Until Windows Answers, So None Never Flashes While Loading
    private IReadOnlyList<AudioDeviceInfo>? _playback;
    private IReadOnlyList<AudioDeviceInfo>? _microphones;
    private DotNetObjectReference<Settings>? _reference;
    private MicrophoneMeter? _meter;
    private bool _testStarting;
    private int _testStart;
    private IDisposable? _deviceWatch;
    private Timer? _deviceTimer;
    private bool _closed;
    private Timer? _chimeTimer;
    private Timer? _volumeTimer;
    private double? _slidVolume;
    private bool _capturingKey;
    private Hotkey? _pendingKey;
    private string? _keysHint;
    private (long Used, long Free)? _space;
    private long? _originalsBytes;
    private bool _listingOriginals;
    private bool _revealStorage;

    private string? SpaceText =>
        _space is var (used, free)
            ? $"{Formatter.FormatSize(used)} Used · {Formatter.FormatSize(free)} Free"
            : null;

    // A Folder Under The Profile Reads From There, Like Videos\LetsClip
    private string ClipsFolderText
    {
        get
        {
            var folder = Controller.Settings.ClipsDirectory;
            var profile =
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                + Path.DirectorySeparatorChar;
            return folder.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
                ? folder[profile.Length..]
                : folder;
        }
    }

    // Only A Game Window's Recording Knows Its Game's Sound
    private bool CanRecordOnlyGameAndDiscord =>
        Controller.Settings.GamesOnly
        && Controller.Settings.SystemAudio
        && AudioMixer.CanRecordProcesses;

    // The Game's Own Sound Ignores The Output Device
    private bool PlaybackDeviceDisabled =>
        !Controller.Settings.SystemAudio
        || (CanRecordOnlyGameAndDiscord && Controller.Settings.OnlyGameAndDiscord);

    // The Saved Keys, Or While Picking The Last Keys Pressed
    private string HotkeyButtonText =>
        !_capturingKey ? Shell.HotkeyText : _pendingKey?.ToString() ?? "Waiting for Keys";

    // A Key Needing A Modifier While Picking, Else Taken Keys
    private string? HotkeyChipText =>
        _capturingKey ? (_keysHint is null ? null : "Add Ctrl, Alt or Win")
        : Shell.HotkeyProblem is null ? null
        : "Taken by Another App";

    private string HotkeyChipTooltip => _capturingKey ? _keysHint ?? "" : "Change Hotkey";

    protected override async Task OnInitializedAsync()
    {
        Controller.Changed += OnChanged;
        Shell.HotkeyChanged += OnChanged;

        await RefreshDevicesAsync();
        await MeasureSpaceAsync();

        // Revealed Once Loaded, So Storage Lands Where It Stays
        _revealStorage = new Uri(Navigation.Uri).Fragment == "#storage";
        _deviceTimer = new Timer(_ => OnDevicesSettled());
        _deviceWatch = await Task.Run(() => AudioDevices.WatchChanges(OnDevicesChanged));
        if (_closed)
        {
            _deviceWatch.Dispose();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_revealStorage)
        {
            _revealStorage = false;
            await JavaScript.CallAsync("letsClip.reveal", "storage");
        }
    }

    private async Task RefreshDevicesAsync()
    {
        // Device Lists Come From WASAPI Off The UI Thread
        (_playback, _microphones) = await Task.Run(() =>
            (AudioDevices.Playback(), AudioDevices.Microphones())
        );
    }

    private void OnDevicesChanged()
    {
        if (!_closed)
        {
            _deviceTimer?.Change(s_deviceSettle, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDevicesSettled()
    {
        _ = InvokeAsync(async () =>
        {
            await RefreshDevicesAsync();
            StateHasChanged();
        });
    }

    private async Task MeasureSpaceAsync()
    {
        // Walks The Folder, Off The UI Thread
        var folder = Controller.Settings.ClipsDirectory;
        (_space, _originalsBytes) = await Task.Run(() =>
            (ClipLibrary.SpaceOf(folder), ClipLibrary.OriginalsSizeOf(folder))
        );
    }

    private void SetDeleteOriginals(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { DeleteOriginals = enabled });
    }

    private async Task DeleteOriginalsAsync()
    {
        // Names The Clips That Lose Their Way Back
        var folder = Controller.Settings.ClipsDirectory;
        _listingOriginals = true;
        IReadOnlyList<ClipInfo> listed;
        try
        {
            listed = await Task.Run(() => Controller.Library.ListAsync(CancellationToken.None));
        }
        finally
        {
            _listingOriginals = false;
        }

        List<ClipInfo> clips = [.. listed.Where(clip => clip.HasOriginal)];
        if (
            !await YesNoDialog.ConfirmAsync(
                DialogService,
                "Delete Kept Originals",
                clips.Count > 0
                    ? "These cut clips can no longer be restored"
                    : "Cut clips can no longer be restored",
                clips: clips
            )
        )
        {
            return;
        }

        var freed = await Task.Run(() => ClipLibrary.DeleteOriginals(folder));
        Snackbar.Add(
            $"Deleted the kept originals, {Formatter.FormatSize(freed)} freed",
            Severity.Success
        );
        await MeasureSpaceAsync();
    }

    #region Audio Devices
    private void SetSystemAudio(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { SystemAudio = enabled });
    }

    private void SetPlaybackDevice(string id)
    {
        Controller.UpdateSettings(settings => settings with { SystemAudioDeviceId = id });
    }

    private void SetOnlyGameAndDiscord(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { OnlyGameAndDiscord = enabled });
    }

    private void SetMicrophone(bool enabled)
    {
        if (!enabled)
        {
            StopMicrophoneTest();
        }

        Controller.UpdateSettings(settings => settings with { Microphone = enabled });
    }

    private async Task SetNoiseCancellationAsync(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { NoiseCancellation = enabled });
        await RestartMicrophoneTestAsync();
    }

    private double MicrophoneVolume => _slidVolume ?? Controller.Settings.MicrophoneVolume;

    private string MicrophoneVolumeText => $"{MicrophoneVolume * 100:0}%";

    private void SetMicrophoneVolume(double level)
    {
        // Shown At Once, Applied Once The Slider Rests
        _slidVolume = Math.Round(level, 1);
        _volumeTimer ??= new Timer(_ => _ = InvokeAsync(ApplyMicrophoneVolumeAsync));
        _volumeTimer.Change(s_deviceSettle, Timeout.InfiniteTimeSpan);
    }

    private async Task ApplyMicrophoneVolumeAsync()
    {
        if (_slidVolume is not { } level)
        {
            return;
        }

        _slidVolume = null;
        Controller.UpdateSettings(settings => settings with { MicrophoneVolume = level });
        if (!_closed)
        {
            await RestartMicrophoneTestAsync();
        }
    }

    private async Task SetMicrophoneDevice(string id)
    {
        Controller.UpdateSettings(settings => settings with { MicrophoneDeviceId = id });
        await RestartMicrophoneTestAsync();
    }

    private async Task RestartMicrophoneTestAsync()
    {
        // A Running Test Follows What Was Just Picked
        if (_meter is not null || _testStarting)
        {
            StopMicrophoneTest();
            await ToggleMicrophoneTestAsync();
        }
    }

    private async Task ToggleMicrophoneTestAsync()
    {
        if (_meter is not null || _testStarting)
        {
            StopMicrophoneTest();
            return;
        }

        // Opening The Devices Takes A Moment, Off The UI Thread
        var id = Controller.Settings.MicrophoneDeviceId;
        var noiseCancellation = Controller.Settings.NoiseCancellation;
        var volume = Controller.Settings.MicrophoneVolume;
        var start = ++_testStart;
        _testStarting = true;
        try
        {
            var meter = await Task.Run(() => new MicrophoneMeter(id, noiseCancellation, volume));

            // Stopped Or Left Meanwhile, It Never Starts
            if (start != _testStart)
            {
                meter.Dispose();
                return;
            }

            (_meter, _testStarting) = (meter, false);
            Snackbar.Add(
                "You hear yourself as clips will, headphones avoid an echo",
                Severity.Info
            );
            _ = WarnIfSilentAsync(meter);
            _ = KeepOutOfClipsAsync(meter);
            _ = StopWhenLostAsync(meter);
        }
        catch (Exception exception)
            when (exception
                    is System.Runtime.InteropServices.COMException
                        or InvalidOperationException
                        or ArgumentException
                        or System.ComponentModel.Win32Exception
            )
        {
            s_logger.Warn($"Failed To Test Microphone {exception.GetType().Name}");
            if (start == _testStart)
            {
                _testStarting = false;
                Snackbar.Add(
                    "The microphone could not be opened, check it in Windows",
                    Severity.Warning
                );
            }
        }
    }

    private async Task WarnIfSilentAsync(MicrophoneMeter meter)
    {
        // Three Seconds Of Pure Silence Means Something Blocks It
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_meter == meter && !meter.HeardSound)
        {
            s_logger.Warn("Microphone Test Heard Only Silence");
            Snackbar.Add(
                "No sound reaches LetsClip from this microphone, check its mute key and Windows privacy settings",
                Severity.Warning
            );
        }
    }

    private async Task KeepOutOfClipsAsync(MicrophoneMeter meter)
    {
        // The Loopback Hears Silence, So No Clip Holds The Test
        while (_meter == meter)
        {
            Controller.Recorder.MuteSystemAudio(s_loopbackMute);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task StopWhenLostAsync(MicrophoneMeter meter)
    {
        // An Unplugged Device Ends The Test, Saying Why
        await meter.Ended;
        if (_meter == meter)
        {
            s_logger.Warn("Microphone Test Lost Its Device");
            Snackbar.Add(
                "The test stopped, check your microphone and output in Windows",
                Severity.Warning
            );
            StopMicrophoneTest();
            StateHasChanged();
        }
    }

    private void StopMicrophoneTest()
    {
        // A Start Still Under Way Is Told It Lost
        _testStart++;
        _testStarting = false;
        _meter?.Dispose();
        _meter = null;
    }
    #endregion

    #region Saving
    private void ToggleSaveSound()
    {
        // Turning It On Plays It, So The Press Answers
        var enabled = !Controller.Settings.SaveSound;
        Controller.UpdateSettings(settings => settings with { SaveSound = enabled });
        _chimeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (enabled)
        {
            PlaySaveSound();
        }
    }

    private void SetSaveSoundVolume(double level)
    {
        // Zero Turns It Off, The Last Level Stays
        Controller.UpdateSettings(settings =>
            settings with
            {
                SaveSound = level > 0,
                SaveSoundVolume = level > 0 ? level : settings.SaveSoundVolume,
            }
        );
        _chimeTimer ??= new Timer(_ => PlaySaveSound());
        _chimeTimer.Change(
            level > 0 ? s_chimeDelay : Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    private void PlaySaveSound()
    {
        SaveChime.Play(Controller.Recorder, Controller.Settings.SaveSoundVolume);
    }

    private async Task StartKeysAsync()
    {
        if (_capturingKey)
        {
            return;
        }

        // The Old Hotkey Lets Go So Keys Reach The Page
        (_keysHint, _pendingKey, _capturingKey) = (null, null, true);
        Shell.PauseHotkey();
        _reference ??= DotNetObjectReference.Create(this);
        await JavaScript.CallAsync("letsClip.watchKeys", _reference);
    }

    [JSInvokable]
    public void OnKeys(string code, string key, bool control, bool alt, bool shift, bool meta)
    {
        // Each Press Replaces The Last, A Slip Is Pressed Again
        _pendingKey = Hotkey.FromBrowser(code, key, control, alt, shift, meta);
        _keysHint = _pendingKey is { IsUsable: true } ? null : "Or Pick A Function Key Alone";
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnKeysDone(bool keep)
    {
        // Enter Keeps The Keys, Escape Or Hiding The Window Cancels
        await (keep ? ConfirmKeysAsync() : CancelKeysAsync());
        StateHasChanged();
    }

    private async Task ConfirmKeysAsync()
    {
        if (_pendingKey is not { IsUsable: true } chosen)
        {
            return;
        }

        await StopKeysAsync();
        if (Shell.ApplyHotkey(chosen))
        {
            Controller.UpdateSettings(settings => settings with { Hotkey = chosen.ToString() });
        }
        else
        {
            // The New Keys Are Taken, The Old Ones Come Back
            RestoreHotkey();
            Snackbar.Add(
                $"{chosen} is taken by another app, kept {Controller.Settings.Hotkey}",
                Severity.Warning
            );
        }
    }

    private async Task CancelKeysAsync()
    {
        await StopKeysAsync();
        _keysHint = null;
        RestoreHotkey();
    }

    private void RestoreHotkey()
    {
        _ = Shell.ApplyHotkey(Hotkey.Parse(Controller.Settings.Hotkey));
    }

    private async Task StopKeysAsync()
    {
        (_capturingKey, _pendingKey) = (false, null);
        await JavaScript.CallAsync("letsClip.stopKeys");
    }
    #endregion

    #region Recording And Storage
    private void SetGamesOnly(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { GamesOnly = enabled });
    }

    private void SetStartWithWindows(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { StartWithWindows = enabled });
    }

    private async Task PickFolderAsync()
    {
        if (DesktopShell.PickFolder(Controller.Settings.ClipsDirectory) is not { } folder)
        {
            return;
        }

        // The Cache Is Wiped Freely, So Clips Never Live There
        var full = Path.GetFullPath(folder);
        if (FilePaths.IsInside(full, AppInfo.LocalDirectory))
        {
            Snackbar.Add("Pick a folder outside LetsClip's own cache", Severity.Warning);
            return;
        }

        if (!ClipLibrary.CanWriteTo(full))
        {
            Snackbar.Add("LetsClip cannot write there, pick another folder", Severity.Warning);
            return;
        }

        Controller.UpdateSettings(settings => settings with { ClipsFolder = full });
        await MeasureSpaceAsync();
    }

    private async Task UseDefaultFolderAsync()
    {
        Controller.UpdateSettings(settings => settings with { ClipsFolder = null });
        await MeasureSpaceAsync();
    }

    private void OpenFolder()
    {
        Explorer.OpenFolder(Controller.Settings.ClipsDirectory);
    }
    #endregion

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        _closed = true;
        _deviceWatch?.Dispose();
        _deviceTimer?.Dispose();
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;
        StopMicrophoneTest();

        // Leaving Mid Pick Restores The Hotkey And Stops Listening
        if (_capturingKey)
        {
            RestoreHotkey();
            _ = StopKeysAsync();
        }

        // A Volume Slid Just Before Leaving Still Lands
        _volumeTimer?.Dispose();
        _ = ApplyMicrophoneVolumeAsync();
        _reference?.Dispose();
        _chimeTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
