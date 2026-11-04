using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Desktop.Theming;
using LetsClip.Services.Capture;
using Microsoft.Web.WebView2.Core;
using MudBlazor.Utilities;
using NLog;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LetsClip.Desktop;

public partial class MainWindow : Window
{
    private const int BalloonMilliseconds = 4000;

    // The Chime Starts A Moment Late, Its Mute Covers That
    private static readonly TimeSpan s_chimeMargin = TimeSpan.FromMilliseconds(400);

    // A Quick Hide And Show Never Suspends
    private static readonly TimeSpan s_suspendDelay = TimeSpan.FromSeconds(1);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly CaptureController _controller;
    private readonly DesktopShell _shell;
    private readonly Forms.NotifyIcon _trayIcon;

    // Tray Icons Per State, Drawn Once
    private readonly Drawing.Icon _plainIcon;
    private readonly Drawing.Icon _bufferingIcon;
    private readonly Drawing.Icon _problemIcon;

    private WindowMessages? _messages;
    private CoreWebView2? _browser;
    private string? _mappedClipsFolder;
    private string? _lastSavedClip;
    private bool _isExiting;

    public MainWindow(IServiceProvider services, CaptureController controller, DesktopShell shell)
    {
        InitializeComponent();
        _controller = controller;
        _shell = shell;
        WebView.Services = services;
        WebView.BlazorWebViewInitialized += (_, eventArgs) =>
            SetUpBrowser(eventArgs.WebView.CoreWebView2);
        SourceInitialized += (_, _) => WindowCaptionTheme.Apply(this);
        StateChanged += (_, _) => HideWhenMinimized();
        IsVisibleChanged += (_, _) => _ = RestPageAsync();

        _plainIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        _bufferingIcon = WithDot(_plainIcon, ToDrawingColor(AppTheme.Custom.PaletteDark.Error));
        _problemIcon = WithDot(_plainIcon, ToDrawingColor(AppTheme.Custom.PaletteDark.Warning));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _plainIcon,
            Text = AppInfo.DisplayName,
            Visible = true,
        };
        _trayIcon.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                ShowFromTray();
            }
            else if (eventArgs.Button == Forms.MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        };
        _trayIcon.BalloonTipClicked += (_, _) => OpenLastClip();

        controller.Recorder.StatusChanged += () => Dispatcher.InvokeAsync(UpdateTray);
        controller.Recorder.ClipSaved += clip => Dispatcher.InvokeAsync(() => NotifySaved(clip));
        controller.Changed += () => Dispatcher.InvokeAsync(MapClipsFolder);
        UpdateTray();
    }

    public void Attach()
    {
        // The Handle Exists Before Showing, So The Hotkey Works Hidden
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _messages = new WindowMessages(handle);
        _messages.HotkeyPressed += () => _ = SaveFromHotkeyAsync();
        _messages.DisplayPowerChanged += isOn =>
            _ = isOn
                ? _controller.ReleaseAsync(CaptureHolds.DisplayOff)
                : _controller.HoldAsync(
                    CaptureHolds.DisplayOff,
                    "Stopped because the display turned off, history was cleared"
                );
        _shell.Attach(_messages, _controller.Settings.ClipsDirectory);
        _shell.ApplyHotkey(Hotkey.Parse(_controller.Settings.Hotkey) ?? Hotkey.Default);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public async Task ExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        // Capture Really Stops, A Running Save Finishes, Saved Clips Stay
        _isExiting = true;
        Hide();
        await _controller.StopAsync();
        await _controller.Recorder.WaitForSavesAsync(TimeSpan.FromSeconds(15));
        ExitNow();
    }

    public void ExitNow()
    {
        _isExiting = true;
        _messages?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _controller.Dispose();
        Application.Current.Shutdown();
    }

    private async Task SaveFromHotkeyAsync()
    {
        // The Hotkey Never Steals Focus, Only Failures Speak Here
        var outcome = await _controller.SaveAsync();
        if (outcome.Error is { } error)
        {
            await Dispatcher.InvokeAsync(() =>
                _trayIcon.ShowBalloonTip(
                    BalloonMilliseconds,
                    "Clip Not Saved",
                    error,
                    Forms.ToolTipIcon.Warning
                )
            );
        }
    }

    private void NotifySaved(ClipInfo clip)
    {
        _lastSavedClip = clip.Path;

        // A Soft Chime, Muted Out Of The Next Clip
        if (_controller.Settings.SaveSound)
        {
            _controller.Recorder.MuteSystemAudio(SaveChime.Duration + s_chimeMargin);
            SaveChime.Play();
        }

        TrayBalloon.ShowQuiet(
            _trayIcon,
            BalloonMilliseconds,
            "Clip Saved",
            $"{Formatter.FormatDuration(clip.Duration)} clip, click to open"
        );
    }

    private void OpenLastClip()
    {
        ShowFromTray();
        if (_lastSavedClip is { } path)
        {
            _shell.RequestOpenClip(path);
        }
    }

    private void ShowTrayMenu()
    {
        var status = _controller.Recorder.Status;
        var seconds = _controller.Settings.ReplaySeconds;
        var open = new TrayMenuItem("Open LetsClip");
        var save = new TrayMenuItem($"Save Last {seconds} Seconds", status.IsBuffering);
        var toggle = new TrayMenuItem(
            _controller.IsPaused ? "Resume Buffering" : "Pause Buffering",
            _controller.Settings.HasSeenCaptureNotice
        );
        var exit = new TrayMenuItem("Exit");
        var picked = TrayMenu.Show(
            new WindowInteropHelper(this).EnsureHandle(),
            open,
            save,
            toggle,
            null,
            exit
        );
        if (picked == open)
        {
            ShowFromTray();
        }
        else if (picked == save)
        {
            _ = SaveFromHotkeyAsync();
        }
        else if (picked == toggle)
        {
            _ = ToggleBufferingAsync();
        }
        else if (picked == exit)
        {
            _ = ExitAsync();
        }
    }

    private async Task ToggleBufferingAsync()
    {
        // The Tray Pauses And Resumes, Buffering Runs By Itself
        if (_controller.IsPaused)
        {
            await _controller.StartAsync();
        }
        else
        {
            await _controller.StopAsync();
        }
    }

    private void UpdateTray()
    {
        var status = _controller.Recorder.Status;
        _trayIcon.Icon = status.State switch
        {
            CaptureState.Buffering or CaptureState.Starting => _bufferingIcon,
            CaptureState.Error => _problemIcon,
            _ => _plainIcon,
        };
        var text = status.State switch
        {
            CaptureState.Buffering =>
                $"{AppInfo.DisplayName}, Buffering {status.RequestedSeconds}s",
            CaptureState.Starting => $"{AppInfo.DisplayName}, Starting",
            CaptureState.Stopping => $"{AppInfo.DisplayName}, Stopping",
            CaptureState.Error => $"{AppInfo.DisplayName}, Error, {status.Message}",
            _ => $"{AppInfo.DisplayName}, {_controller.IdleText}",
        };
        _trayIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void SetUpBrowser(CoreWebView2 browser)
    {
        _browser = browser;

        // Page Background Before The First Paint, No White Flash
        WebView.WebView.DefaultBackgroundColor = Drawing.Color.FromArgb(0x20, 0x20, 0x20);

        // Browser Keys, Ctrl Wheel And Pinch Zoom Stay Off
        browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
        browser.Settings.IsZoomControlEnabled = false;
        browser.Settings.IsPinchZoomEnabled = false;
        browser.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;

        Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
        browser.SetVirtualHostNameToFolderMapping(
            DesktopShell.ThumbnailsHost,
            AppInfo.ThumbnailsDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors
        );
        MapClipsFolder();
    }

    private void MapClipsFolder()
    {
        // Only The Clips Folder Is Reachable, Streamed With Range Requests
        var folder = _controller.Settings.ClipsDirectory;
        _shell.SetClipsFolder(folder);
        if (
            _browser is null
            || string.Equals(folder, _mappedClipsFolder, StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            if (_mappedClipsFolder is not null)
            {
                _browser.ClearVirtualHostNameToFolderMapping(DesktopShell.ClipsHost);
            }

            _browser.SetVirtualHostNameToFolderMapping(
                DesktopShell.ClipsHost,
                folder,
                CoreWebView2HostResourceAccessKind.DenyCors
            );
            _mappedClipsFolder = folder;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The Library Shows The Folder Problem, The Window Stays Up
            s_logger.Warn($"Failed To Map Clips Folder {exception.GetType().Name}");
        }
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

    private void HideWhenMinimized()
    {
        // Minimising Goes Straight To The Tray, Capture Keeps Running
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private async Task RestPageAsync()
    {
        if (WebView.WebView is not { CoreWebView2: { } browser } control)
        {
            return;
        }

        if (IsVisible)
        {
            control.Visibility = Visibility.Visible;
            ResumePage(browser);
            return;
        }

        // In The Tray The Page Sleeps, Recording Lives Outside It
        control.Visibility = Visibility.Hidden;
        browser.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        await Task.Delay(s_suspendDelay);
        if (IsVisible)
        {
            return;
        }

        await browser.TrySuspendAsync();
        if (IsVisible)
        {
            ResumePage(browser);
        }
    }

    private static void ResumePage(CoreWebView2 browser)
    {
        if (browser.IsSuspended)
        {
            browser.Resume();
        }

        browser.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        // Closing Hides To The Tray, Only Exit Ends LetsClip
        if (!_isExiting)
        {
            eventArgs.Cancel = true;
            Hide();
            if (!_controller.Settings.HasSeenTrayHint)
            {
                _controller.UpdateSettings(settings => settings with { HasSeenTrayHint = true });
                _trayIcon.ShowBalloonTip(
                    BalloonMilliseconds,
                    "LetsClip Is Still Running",
                    _controller.IsBuffering
                        ? "Buffering continues in the tray, right click the icon to stop or exit"
                        : "It waits in the tray, right click the icon to start buffering or exit",
                    Forms.ToolTipIcon.Info
                );
            }
        }

        base.OnClosing(eventArgs);
    }
}
