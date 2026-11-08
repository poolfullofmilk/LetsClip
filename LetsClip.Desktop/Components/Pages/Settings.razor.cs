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

    private IReadOnlyList<AudioDeviceInfo> _playback = [];
    private IReadOnlyList<AudioDeviceInfo> _microphones = [];
    private DotNetObjectReference<Settings>? _reference;
    private MicrophoneMeter? _meter;
    private bool _testStarting;
    private int _testStart;
    private IDisposable? _deviceWatch;
    private Timer? _deviceTimer;
    private bool _closed;
    private Timer? _chimeTimer;
    private bool _capturingKey;
    private Hotkey? _pendingKey;
    private string? _folderError;
    private string? _hotkeyError;
    private string? _microphoneError;
    private (long Used, long Free)? _space;

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

    // The Saved Keys, Or While Picking The Last Keys Pressed
    private string HotkeyButtonText =>
        !_capturingKey ? Shell.HotkeyText : _pendingKey?.ToString() ?? "Waiting for Keys";

    protected override async Task OnInitializedAsync()
    {
        Controller.Changed += OnChanged;
        Shell.HotkeyChanged += OnChanged;

        await RefreshDevicesAsync();
        await MeasureSpaceAsync();
        _deviceTimer = new Timer(_ => OnDevicesSettled());
        _deviceWatch = await Task.Run(() => AudioDevices.WatchChanges(OnDevicesChanged));
        if (_closed)
        {
            _deviceWatch.Dispose();
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
        _space = await Task.Run(() => ClipLibrary.SpaceOf(folder));
    }

    private async Task ShowGamesAsync()
    {
        await DialogService.ShowAsync<GamesDialog>(
            (string?)null,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true }
        );
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
        _microphoneError = null;

        if (_meter is not null || _testStarting)
        {
            StopMicrophoneTest();
            return;
        }

        // Opening The Devices Takes A Moment, Off The UI Thread
        var id = Controller.Settings.MicrophoneDeviceId;
        var noiseCancellation = Controller.Settings.NoiseCancellation;
        var start = ++_testStart;
        _testStarting = true;
        try
        {
            var meter = await Task.Run(() => new MicrophoneMeter(id, noiseCancellation));

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
                _microphoneError = "The microphone could not be opened, check it in Windows";
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
            _microphoneError =
                "No sound reaches LetsClip from this microphone, check its mute key and Windows privacy settings";
            StateHasChanged();
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
            _microphoneError = "The test stopped, check your microphone and output in Windows";
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
        (_hotkeyError, _pendingKey, _capturingKey) = (null, null, true);
        Shell.PauseHotkey();
        _reference ??= DotNetObjectReference.Create(this);
        await JavaScript.InvokeVoidAsync("letsClip.watchKeys", _reference);
    }

    [JSInvokable]
    public void OnKeys(string code, string key, bool control, bool alt, bool shift, bool meta)
    {
        // Each Press Replaces The Last, A Slip Is Pressed Again
        _pendingKey = Hotkey.FromBrowser(code, key, control, alt, shift, meta);
        _hotkeyError = _pendingKey is { IsUsable: true }
            ? null
            : "Use Ctrl, Alt or Win with a key, or a function key alone";
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnKeysDone(bool keep)
    {
        // Enter Keeps The Keys, Escape Or Hiding The Window Cancels
        return keep ? ConfirmKeysAsync() : CancelKeysAsync();
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
            _hotkeyError = $"{chosen} is taken by another app, kept {Controller.Settings.Hotkey}";
        }

        StateHasChanged();
    }

    private async Task CancelKeysAsync()
    {
        await StopKeysAsync();
        _hotkeyError = null;
        RestoreHotkey();
        StateHasChanged();
    }

    private void RestoreHotkey()
    {
        _ = Shell.ApplyHotkey(Hotkey.Parse(Controller.Settings.Hotkey));
    }

    private async Task StopKeysAsync()
    {
        (_capturingKey, _pendingKey) = (false, null);
        try
        {
            await JavaScript.InvokeVoidAsync("letsClip.stopKeys");
        }
        catch (JSDisconnectedException)
        {
            // The Window Is Closing, Its Listener Goes With It
        }
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
        _folderError = null;
        if (DesktopShell.PickFolder(Controller.Settings.ClipsDirectory) is not { } folder)
        {
            return;
        }

        // The Cache Is Wiped Freely, So Clips Never Live There
        var full = Path.GetFullPath(folder);
        if (FilePaths.IsInside(full, AppInfo.LocalDirectory))
        {
            _folderError = "Pick a folder outside LetsClip's own cache";
            return;
        }

        if (!ClipLibrary.CanWriteTo(full))
        {
            _folderError = "LetsClip cannot write there, pick another folder";
            return;
        }

        Controller.UpdateSettings(settings => settings with { ClipsFolder = full });
        await MeasureSpaceAsync();
    }

    private async Task UseDefaultFolderAsync()
    {
        _folderError = null;
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

        _reference?.Dispose();
        _chimeTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
