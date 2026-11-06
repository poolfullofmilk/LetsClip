using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Common;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using NLog;

namespace LetsClip.Desktop.Components.Pages;

public partial class Settings : IDisposable
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required IJSRuntime JavaScript { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    private IReadOnlyList<AudioDeviceInfo> _playback = [];
    private IReadOnlyList<AudioDeviceInfo> _microphones = [];
    private DotNetObjectReference<Settings>? _reference;
    private MicrophoneMeter? _meter;
    private bool _capturingKey;
    private Hotkey? _pendingKey;
    private string? _folderError;
    private string? _hotkeyError;
    private string? _microphoneError;

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

        // Device Lists Come From WASAPI Off The UI Thread
        (_playback, _microphones) = await Task.Run(() =>
            (AudioDevices.Playback(), AudioDevices.Microphones())
        );
    }

    private async Task ShowGamesAsync()
    {
        await DialogService.ShowAsync<GamesDialog>(
            (string?)null,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true }
        );
    }

    #region Audio Devices
    private static string LabelOf(IReadOnlyList<AudioDeviceInfo> devices, string? id)
    {
        return devices.FirstOrDefault(device => device.Id == id)?.Name ?? "Automatic";
    }

    private static string ShownDevice(IReadOnlyList<AudioDeviceInfo> devices, string id)
    {
        // A Gone Device Records Through Automatic Until It Returns
        return devices.Any(device => device.Id == id) ? id : AudioDevices.AutomaticId;
    }

    private static string? DetailOf(IReadOnlyList<AudioDeviceInfo> devices, string id)
    {
        // The Closed Field Reads Like Its Item
        return devices.FirstOrDefault(device => device.Id == id) is { } device
            ? device.Adapter
            : DefaultName(devices);
    }

    private static string? DefaultName(IReadOnlyList<AudioDeviceInfo> devices)
    {
        return devices.FirstOrDefault(device => device.IsDefault)?.Name;
    }

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

    private void SetNoiseCancellation(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { NoiseCancellation = enabled });
    }

    private async Task SetMicrophoneDevice(string id)
    {
        Controller.UpdateSettings(settings => settings with { MicrophoneDeviceId = id });

        // A Running Test Follows The Newly Picked Microphone
        if (_meter is not null)
        {
            StopMicrophoneTest();
            await ToggleMicrophoneTestAsync();
        }
    }

    private async Task ToggleMicrophoneTestAsync()
    {
        _microphoneError = null;
        if (_meter is not null)
        {
            StopMicrophoneTest();
            return;
        }

        // Opening The Device Takes A Moment, Off The UI Thread
        var id = Controller.Settings.MicrophoneDeviceId;
        try
        {
            _meter = await Task.Run(() => new MicrophoneMeter(id));
            _ = WarnIfSilentAsync(_meter);
        }
        catch (Exception exception)
            when (exception
                    is System.Runtime.InteropServices.COMException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            s_logger.Warn($"Failed To Test Microphone {exception.GetType().Name}");
            _microphoneError = "The microphone could not be opened, check it in Windows";
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

    private void StopMicrophoneTest()
    {
        _meter?.Dispose();
        _meter = null;
    }
    #endregion

    #region Saving
    private void SetSaveSound(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { SaveSound = enabled });
    }

    private void SetSaveSoundVolume(double level)
    {
        // Each Step Plays At Its New Level
        Controller.UpdateSettings(settings => settings with { SaveSoundVolume = level });
        SaveChime.Play(Controller.Recorder, level);
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
    public void OnKeys(string code, bool control, bool alt, bool shift, bool meta)
    {
        // Each Press Replaces The Last, A Slip Is Pressed Again
        _pendingKey = Hotkey.FromBrowser(code, control, alt, shift, meta);
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
            _ = Shell.ApplyHotkey(Hotkey.Parse(Controller.Settings.Hotkey));
            _hotkeyError = $"{chosen} is taken by another app, kept {Controller.Settings.Hotkey}";
        }

        StateHasChanged();
    }

    private async Task CancelKeysAsync()
    {
        await StopKeysAsync();
        _hotkeyError = null;
        _ = Shell.ApplyHotkey(Hotkey.Parse(Controller.Settings.Hotkey));
        StateHasChanged();
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

    private void PickFolder()
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

        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".LetsClip-{Guid.NewGuid():N}.probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _folderError = "LetsClip cannot write there, pick another folder";
            return;
        }

        Controller.UpdateSettings(settings => settings with { ClipsFolder = full });
    }

    private void UseDefaultFolder()
    {
        _folderError = null;
        Controller.UpdateSettings(settings => settings with { ClipsFolder = null });
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
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;
        StopMicrophoneTest();

        // Leaving Mid Pick Restores The Hotkey And Stops Listening
        if (_capturingKey)
        {
            _ = Shell.ApplyHotkey(Hotkey.Parse(Controller.Settings.Hotkey));
            _ = StopKeysAsync();
        }

        _reference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
