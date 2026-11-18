using System.ComponentModel;
using System.Net.Http;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Pages;
using LetsClip.Desktop.Interop;
using LetsClip.Desktop.Theming;
using LetsClip.Services.Capture;
using LetsClip.Services.Games;
using LetsClip.Services.Integration;
using LetsClip.Services.Media;
using MudBlazor.Utilities;
using NLog;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LetsClip.Desktop;

public sealed class Tray
{
    private const int BalloonMilliseconds = 4000;
    private const string RecordingNotSaved = "Recording Not Saved";

    // Looked For This Often, And Whenever The Window Opens
    private static readonly TimeSpan s_updateCheckPeriod = TimeSpan.FromHours(6);

    // An Unanswered Check Is Asked Again After This
    private static readonly TimeSpan s_updateRetryPeriod = TimeSpan.FromMinutes(5);

    // Hidden This Long Is Idle, A Found Update Installs
    private static readonly TimeSpan s_idleBeforeUpdate = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_idleCheckPeriod = TimeSpan.FromMinutes(1);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly MainWindow _window;
    private readonly CaptureController _controller;
    private readonly DesktopShell _shell;
    private readonly Forms.NotifyIcon _icon;

    // Its Own Plain Window, A Tray Start Builds None
    private readonly WindowMessages _messages = new();

    // Tray Icons Per State, Drawn Once
    private readonly Drawing.Icon _plainIcon;
    private readonly Drawing.Icon _recordingIcon;
    private readonly Drawing.Icon _problemIcon;

    // What A Click On The Latest Balloon Does
    private Action? _balloonClick;
    private bool _isUpdating;
    private Task<ReleaseInfo?>? _updateCheck;

    // The Game Last Announced, Restarts Never Repeat It
    private string? _announcedGame;

    public Tray(MainWindow window, CaptureController controller, DesktopShell shell)
    {
        _window = window;
        _controller = controller;
        _shell = shell;
        _plainIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        _recordingIcon = WithDot(_plainIcon, ToDrawingColor(AppTheme.Custom.PaletteDark.Error));
        _problemIcon = WithDot(_plainIcon, ToDrawingColor(AppTheme.Custom.PaletteDark.Warning));
        _icon = new Forms.NotifyIcon
        {
            Icon = _plainIcon,
            Text = AppInfo.DisplayName,
            Visible = true,
        };
        _icon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                _window.ShowFromTray();
            }
            else if (eventArgs.Button == Forms.MouseButtons.Right)
            {
                ShowMenu();
            }
        };
        _icon.BalloonTipClicked += (_, _) => (_balloonClick ?? _window.ShowFromTray)();

        controller.Recorder.StatusChanged += () => _window.Dispatcher.InvokeAsync(UpdateIcon);

        // A Chime At The Press, A Falling One On Failure
        controller.Recorder.SaveStarted += () => Chime(failed: false);
        controller.SaveFailed += () => Chime(failed: true);
        controller.Recorder.RecordingNotSaved += error =>
        {
            Chime(failed: true);
            _window.Dispatcher.InvokeAsync(() => ShowBalloon(RecordingNotSaved, error));
        };
        controller.Recorder.ClipSaved += clip =>
            _window.Dispatcher.InvokeAsync(() => NotifySaved(clip));

        // A Game Found Or Lost Changes The Tray's Words
        controller.Changed += () => _window.Dispatcher.InvokeAsync(UpdateIcon);
        UpdateIcon();

        _messages.HotkeyPressed += () => _ = SaveFromHotkeyAsync();
        _messages.DisplayPowerChanged += isOn =>
            _ = isOn
                ? _controller.ReleaseAsync(CaptureHolds.DisplayOff)
                : _controller.HoldAsync(
                    CaptureHolds.DisplayOff,
                    "Stopped because the display turned off, history was cleared"
                );
        _shell.Attach(_messages);

        // Every Open Looks For An Update And New Games
        _window.IsVisibleChanged += (_, _) =>
        {
            if (_window.IsVisible)
            {
                _ = FindUpdateAsync();
                _ = _controller.ScanGamesAsync();
            }
        };
        _controller.Exports.Finished += NotifyExported;
        _ = KeepUpdatedAsync();
        _ = FindChangesAsync();

        // A Tray Start Shows No Panel, So Say It Here
        if (
            !_shell.ApplyHotkey(Hotkey.Parse(_controller.Settings.Hotkey))
            && _shell.HotkeyProblem is { } problem
        )
        {
            ShowBalloon("Save Hotkey Taken", problem);
        }
    }

    #region Exit
    public async Task ExitAsync()
    {
        if (_window.IsExiting)
        {
            return;
        }

        // Capture Stops And A Running Save Finishes
        _window.IsExiting = true;
        _window.Hide();
        await _controller.HoldAsync(CaptureHolds.Exiting, "Stopped because LetsClip is closing");
        // A Long Recording Stopped By Exit Gets Its Save Too
        await _controller.Recorder.WaitForSavesAsync(TimeSpan.FromMinutes(2));

        // A Running Export Ends Cleanly, Its Unfinished File Deleted
        _controller.Exports.Cancel();
        for (var waited = 0; _controller.Exports.IsBusy && waited < 50; waited++)
        {
            await Task.Delay(100);
        }

        // A Clip Deleted Moments Ago Stays Deleted
        Clips.FinishDeletes();
        ExitNow();
    }

    public bool EndSession()
    {
        // Windows Waits Only For A Recording's Save
        if (_controller.RecordingSince is null && !_controller.Recorder.Status.IsSaving)
        {
            Clips.FinishDeletes();
            ExitNow();
            return false;
        }

        var named = _messages.BlockShutdown("Saving the recording");
        s_logger.Info($"Held The Session's End To Save A Recording, Reason Shown {named}");
        _ = ExitAsync();
        return true;
    }

    private void ExitNow()
    {
        // A Window Open At Shutdown Is Remembered Too
        if (_window.IsVisible)
        {
            _window.RememberPlacement();
        }

        _window.IsExiting = true;
        _messages.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _controller.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
    #endregion

    #region Updates
    public async Task InstallUpdateAsync(
        ReleaseInfo release,
        IProgress<(long Received, long Total)>? progress
    )
    {
        // One At A Time, The New Exe Waits For Us
        if (_isUpdating || _window.IsExiting)
        {
            return;
        }

        _isUpdating = true;
        try
        {
            var path = await Task.Run(() => Updater.DownloadAsync(release, progress));

            // Remembered First, One That Cannot Start Is Not Retried
            _controller.UpdateSettings(settings => settings with { AttemptedUpdate = release.Tag });
            Updater.Launch(path, inTray: !_window.IsVisible);
            await ExitAsync();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private async Task KeepUpdatedAsync()
    {
        // At Start And Every Six Hours, Installed Once Idle
        while (!_window.IsExiting)
        {
            var answered = false;
            try
            {
                var latest = await FindUpdateAsync();
                answered = latest is not null;
                if (latest is { } release && release.Version > AppInfo.Version)
                {
                    await InstallWhenIdleAsync(release);
                }
            }
            catch (Exception exception)
            {
                // One Failed Check Never Ends The Checks
                s_logger.Warn($"Failed To Check For Updates {exception.GetType().Name}");
            }

            // No Network Yet At Sign In, Asked Again Soon
            await Task.Delay(answered ? s_updateCheckPeriod : s_updateRetryPeriod);
        }
    }

    private async Task FindChangesAsync()
    {
        // A New Build Shows What It Brought, Once
        var seen = _controller.Settings.SeenCommit;
        if (!Updater.CanInstall || AppInfo.Commit is not { } commit || seen == commit)
        {
            return;
        }

        // Unreadable Now, The Next Start Asks Again
        var changes = seen is null ? [] : await Updater.ChangesAsync(seen, commit);
        if (changes is null)
        {
            return;
        }

        _controller.UpdateSettings(settings => settings with { SeenCommit = commit });
        if (changes is [_, ..])
        {
            _shell.ReportChanges(changes);
        }
    }

    private Task<ReleaseInfo?> FindUpdateAsync()
    {
        // The Start's Check And The First Open Share One Request
        return _updateCheck is { IsCompleted: false } running
            ? running
            : _updateCheck = CheckForUpdateAsync();
    }

    private async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        // A Newer Release Gets Its Dialog And The Red Dot
        var latest = await Updater.CheckAsync();
        if (latest is { } release && release.Version > AppInfo.Version)
        {
            _shell.ReportUpdate(release);
        }

        return latest;
    }

    private async Task InstallWhenIdleAsync(ReleaseInfo release)
    {
        // Debug, Sandboxed And Renamed Builds Only Offer It
        if (!Updater.CanInstall)
        {
            s_logger.Info($"Found Update {release.Tag}, Only A Release Exe Installs It Itself");
            return;
        }

        // It Did Not Start Before, Only A Click Tries Again
        if (_controller.Settings.AttemptedUpdate == release.Tag)
        {
            s_logger.Info($"Skipped Update {release.Tag}, It Did Not Start Before");
            return;
        }

        // Hidden A While, No Save, Export Or Manual Recording
        while (!_window.IsExiting)
        {
            if (
                _window.HiddenFor is { } idle
                && idle >= s_idleBeforeUpdate
                && !_controller.Exports.IsBusy
                && !_controller.Recorder.Status.IsSaving
                && _controller.RecordingSince is null
            )
            {
                s_logger.Info(
                    $"Began Installing Update {release.Tag} In The Background, Hidden For {idle.TotalMinutes:0} Minutes"
                );
                try
                {
                    await InstallUpdateAsync(release, null);
                }
                catch (Exception exception)
                    when (exception
                            is HttpRequestException
                                or IOException
                                or InvalidDataException
                                or OperationCanceledException
                                or UnauthorizedAccessException
                                or Win32Exception
                    )
                {
                    // The Next Check Tries Again, The Running Version Keeps Recording
                    s_logger.Warn($"Failed To Update In The Background {exception.GetType().Name}");
                }

                return;
            }

            await Task.Delay(s_idleCheckPeriod);
        }
    }
    #endregion

    #region Saves And Balloons
    private async Task SaveFromHotkeyAsync()
    {
        // With No Game, The App In Front Is Likely Wanted
        var front =
            _controller.Settings.GamesOnly && _controller.Games.GameName is null
                ? GameWatcher.ForegroundApp()
                : null;

        // The Hotkey Never Steals Focus, Only Failures Speak Here
        var outcome = await Task.Run(_controller.SaveAsync);
        if (outcome.Error is not { } error)
        {
            return;
        }

        await _window.Dispatcher.InvokeAsync(() =>
        {
            if (front is { } app && _controller.Games.GameName is null)
            {
                ShowBalloon(
                    "Clip Not Saved",
                    $"Nothing is being recorded, click to record {app.Name} from now on",
                    () => RecordFromNowOn(app)
                );
                return;
            }

            if (!ToastInGame("Clip Not Saved", error, isWarning: true))
            {
                ShowBalloon("Clip Not Saved", error);
            }
        });
    }

    private async Task StopRecordingAsync()
    {
        // Saved Like A Clip, Problems Show Like A Failed Press
        var outcome = await Task.Run(_controller.StopRecordingAsync);
        if (outcome.Error is { } error)
        {
            await _window.Dispatcher.InvokeAsync(() => ShowBalloon(RecordingNotSaved, error));
        }
    }

    private void RecordFromNowOn(AddedGame app)
    {
        // One Click Turns A Missed Clip Into A Recorded Game
        _controller.SetRecorded([app], true);
        _balloonClick = null;
        TrayBalloon.ShowQuiet(
            _icon,
            BalloonMilliseconds,
            "Added to Games and Apps",
            $"{app.Name} is recorded from now on"
        );
    }

    private void Chime(bool failed)
    {
        // Every Sound Is Ours, Under The One Save Sound Setting
        if (_controller.Settings.SaveSound)
        {
            SaveChime.Play(_controller.Recorder, _controller.Settings.SaveSoundVolume, failed);
        }
    }

    private void ShowBalloon(string title, string text, Action? onClick = null)
    {
        // A Problem Shows Its Warning Sign, Windows' Sound Stays Out
        _balloonClick = onClick;
        TrayBalloon.ShowQuiet(_icon, BalloonMilliseconds, title, text, isWarning: true);
    }

    private void NotifyExported(ExportOutcome outcome)
    {
        // Hidden Meanwhile, The Tray Says How It Ended
        if (_window.IsVisible || (outcome.File is null && outcome.Problem is null))
        {
            return;
        }

        var path = outcome.Request.SourcePath;
        if (outcome is { File: null, Problem: { } problem })
        {
            ShowBalloon("Export Failed", problem, () => _window.OpenClip(path));
            return;
        }

        _balloonClick = () => _window.OpenClip(path);
        TrayBalloon.ShowQuiet(
            _icon,
            BalloonMilliseconds,
            "Clip Exported",
            outcome.Problem ?? "Click to open it and copy the file"
        );
    }

    private bool ToastInGame(string text, string? detail, bool isWarning = false)
    {
        // A Full Screen Game Can Hide Balloons, A Toast Shows
        if (
            MonitorCatalog.FilledBy(_controller.Monitors, GameWatcher.ForegroundWindow())
            is not { } monitor
        )
        {
            return false;
        }

        // Only A Monitor Recording Could Ever Capture The Toast
        MonitorOverlay.Toast(
            monitor,
            text,
            detail,
            isWarning,
            hideFromCapture: _controller.Recorder.Options is { GameWindow: 0 }
        );
        return true;
    }

    private void NotifySaved(ClipInfo clip)
    {
        _ = WarnIfBlackAsync(clip.Path);

        // Off By Default, The Chime Already Answered The Press
        if (
            !_controller.Settings.Notifications
            || ToastInGame("Clip Saved", Formatter.FormatDuration(clip.Duration))
        )
        {
            return;
        }

        _balloonClick = () => _window.OpenClip(clip.Path);
        TrayBalloon.ShowQuiet(
            _icon,
            BalloonMilliseconds,
            "Clip Saved",
            $"{Formatter.FormatDuration(clip.Duration)} clip, click to open"
        );
    }

    private async Task WarnIfBlackAsync(string path)
    {
        // A Game Hiding Its Picture Saves Black Clips, Always Said
        try
        {
            if (!await Task.Run(() => MediaProbe.IsBlackAsync(path, CancellationToken.None)))
            {
                return;
            }
        }
        catch (Exception exception) when (exception is TimeoutException or Win32Exception)
        {
            s_logger.Warn($"Failed To Check A Clip For Black {exception.GetType().Name}");
            return;
        }

        s_logger.Warn($"Saved A Black Clip, {Path.GetFileName(path)}");
        const string detail = "The game may hide its picture, try borderless or windowed mode";
        if (!ToastInGame("Clip Is Black", detail, isWarning: true))
        {
            ShowBalloon("Clip Is Black", detail, () => _window.OpenClip(path));
        }
    }

    private void AnnounceGame()
    {
        // A Newly Recorded Game Says So Once, Restarts Stay Quiet
        var game = _controller.IsRecording ? _controller.Games.GameName : null;
        if (game == _announcedGame)
        {
            return;
        }

        _announcedGame = game;
        if (
            game is null
            || !_controller.Settings.Notifications
            || ToastInGame($"Recording {game}", null)
        )
        {
            return;
        }

        _balloonClick = null;
        TrayBalloon.ShowQuiet(
            _icon,
            BalloonMilliseconds,
            $"Recording {game}",
            $"Press {_controller.Settings.Hotkey} to save a clip"
        );
    }
    #endregion

    #region Icon And Menu
    private void ShowMenu()
    {
        var open = new TrayMenuItem("Open LetsClip");
        var save = new TrayMenuItem(_controller.SaveText, _controller.IsRecording);
        var recording = _controller.RecordingSince is not null;
        var record = new TrayMenuItem(
            recording ? "Stop Recording" : "Record Until Stopped",
            recording || (_controller.IsRecording && !_controller.Recorder.Status.IsSaving)
        );
        var exit = new TrayMenuItem("Exit");
        var picked = TrayMenu.Show(_messages.Handle, open, save, record, null, exit);
        if (picked == open)
        {
            _window.ShowFromTray();
        }
        else if (picked == save)
        {
            _ = SaveFromHotkeyAsync();
        }
        else if (picked == record)
        {
            if (recording)
            {
                _ = StopRecordingAsync();
            }
            else
            {
                _controller.StartRecording();
            }
        }
        else if (picked == exit)
        {
            _ = ExitAsync();
        }
    }

    private void UpdateIcon()
    {
        // The Same Words As The Chip
        var status = _controller.Recorder.Status;
        _icon.Icon =
            _controller.ShowsError ? _problemIcon
            : _controller.IsRecording ? _recordingIcon
            : _plainIcon;
        var text = _controller.ShowsError
            ? $"{AppInfo.DisplayName}, Error, {status.Message}"
            : $"{AppInfo.DisplayName}, {_controller.StatusText}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
        AnnounceGame();
    }

    private static Drawing.Icon WithDot(Drawing.Icon baseIcon, Drawing.Color color)
    {
        using var bitmap = baseIcon.ToBitmap();
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            // A Ringed Dot In The Corner, Readable At 16 Pixels
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var size = bitmap.Width / 2;
            var bounds = new Drawing.Rectangle(
                bitmap.Width - size - 1,
                bitmap.Height - size - 1,
                size,
                size
            );
            using var brush = new Drawing.SolidBrush(color);
            using var ring = new Drawing.Pen(Drawing.Color.FromArgb(0x14, 0x14, 0x14), size / 6f);
            graphics.FillEllipse(brush, bounds);
            graphics.DrawEllipse(ring, bounds);
        }

        // Ponytail: Two Icon Handles Live For The App's Life
        return Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private static Drawing.Color ToDrawingColor(MudColor color)
    {
        return Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }
    #endregion
}
