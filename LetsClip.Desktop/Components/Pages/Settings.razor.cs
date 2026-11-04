using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace LetsClip.Desktop.Components.Pages;

public partial class Settings : IDisposable
{
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
    private bool _capturingKey;
    private string? _folderError;
    private string? _hotkeyError;
    private string? _defaultPlaybackName;

    private const string HotkeyDescription =
        "Saves the last moments from anywhere, held keys save once";

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

    private string GamesLinkText =>
        Controller.GameCount is { } count ? $"{count} found" : "see the list";

    protected override async Task OnInitializedAsync()
    {
        Controller.Changed += OnChanged;
        Shell.HotkeyChanged += OnChanged;

        // Device Lists Come From WASAPI Off The UI Thread
        (_playback, _microphones, _defaultPlaybackName) = await Task.Run(() =>
            (AudioDevices.Playback(), AudioDevices.Microphones(), AudioDevices.DefaultName(true))
        );
    }

    private async Task ShowGamesAsync()
    {
        var dialog = await DialogService.ShowAsync<GamesDialog>(
            (string?)null,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true }
        );
        await dialog.Result;
    }

    private string PlaybackLabel(string? id)
    {
        return id == AudioDevices.DefaultPlaybackId
            ? "Windows Default"
            : _playback.FirstOrDefault(device => device.Id == id)?.Name ?? "";
    }

    private string MicrophoneLabel(string? id)
    {
        return _microphones.FirstOrDefault(device => device.Id == id)?.Name ?? "";
    }

    private void SetSaveSound(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { SaveSound = enabled });
    }

    private void SetGamesOnly(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { GamesOnly = enabled });
    }

    private void SetStartWithWindows(bool enabled)
    {
        Controller.UpdateSettings(settings => settings with { StartWithWindows = enabled });
    }

    private string? HotkeyMessage()
    {
        return _hotkeyError ?? Shell.HotkeyProblem;
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
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
        // Turning It On Picks The Default Once, Never Silently Later
        var id =
            Controller.Settings.MicrophoneDeviceId is { Length: > 0 } known
            && _microphones.Any(device => device.Id == known)
                ? known
                : AudioDevices.DefaultMicrophoneId()
                    ?? (_microphones.Count > 0 ? _microphones[0].Id : null);
        Controller.UpdateSettings(settings =>
            settings with
            {
                Microphone = enabled && id is not null,
                MicrophoneDeviceId = id,
            }
        );
    }

    private void SetMicrophoneDevice(string id)
    {
        Controller.UpdateSettings(settings => settings with { MicrophoneDeviceId = id });
    }

    private async Task CaptureKeyAsync()
    {
        // The Old Hotkey Lets Go So Keys Reach The Page
        _hotkeyError = null;
        _capturingKey = true;
        Shell.PauseHotkey();
        StateHasChanged();
        var pressed = await JavaScript.InvokeAsync<PressedKey?>("letsClip.captureKey");
        _capturingKey = false;
        var hotkey = pressed is null
            ? null
            : Hotkey.FromBrowser(
                pressed.Code,
                pressed.Control,
                pressed.Alt,
                pressed.Shift,
                pressed.Meta
            );
        if (pressed is not null && hotkey is not { IsUsable: true })
        {
            _hotkeyError = "Use Ctrl, Alt or Win with a key, or a function key alone";
        }

        var chosen = hotkey is { IsUsable: true } usable
            ? usable
            : Hotkey.Parse(Controller.Settings.Hotkey) ?? Hotkey.Default;
        if (Shell.ApplyHotkey(chosen))
        {
            Controller.UpdateSettings(settings => settings with { Hotkey = chosen.ToString() });
        }
        else if (hotkey is { IsUsable: true })
        {
            // The New Keys Are Taken, The Old Ones Come Back
            _ = Shell.ApplyHotkey(Hotkey.Parse(Controller.Settings.Hotkey) ?? Hotkey.Default);
            _hotkeyError = $"{chosen} is taken by another app, kept {Controller.Settings.Hotkey}";
        }
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
        if (
            full.StartsWith(
                Path.GetFullPath(AppInfo.LocalDirectory),
                StringComparison.OrdinalIgnoreCase
            )
        )
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

    public void Dispose()
    {
        Controller.Changed -= OnChanged;
        Shell.HotkeyChanged -= OnChanged;

        // Leaving Mid Capture Puts The Saved Hotkey Back
        if (_capturingKey)
        {
            _ = Shell.ApplyHotkey(Hotkey.Parse(Controller.Settings.Hotkey) ?? Hotkey.Default);
        }

        GC.SuppressFinalize(this);
    }

    private sealed record PressedKey(string Code, bool Control, bool Alt, bool Shift, bool Meta);
}
