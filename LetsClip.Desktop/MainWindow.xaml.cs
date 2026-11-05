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

    // A Quick Hide And Show Never Suspends
    private static readonly TimeSpan s_suspendDelay = TimeSpan.FromSeconds(1);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly CaptureController _controller;
    private readonly DesktopShell _shell;
    private readonly Forms.NotifyIcon _trayIcon;

    // Tray Icons Per State, Drawn Once
    private readonly Drawing.Icon _plainIcon;
    private readonly Drawing.Icon _recordingIcon;
    private readonly Drawing.Icon _problemIcon;

    private WindowMessages? _messages;
    private CoreWebView2? _browser;
    private string? _mappedClipsFolder;
    private string? _lastSavedClip;
    private bool _isExiting;
    private WindowState _windowedState;

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
        _recordingIcon = WithDot(_plainIcon, ToDrawingColor(AppTheme.Custom.PaletteDark.Error));
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
        controller.Recorder.SaveStarted += PlaySaveChime;
        controller.Recorder.ClipSaved += clip => Dispatcher.InvokeAsync(() => NotifySaved(clip));
        controller.Changed += () =>
            Dispatcher.InvokeAsync(() =>
            {
                // A Game Found Or Lost Changes The Tray's Words
                MapClipsFolder();
                UpdateTray();
            });
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
        _shell.Attach(_messages);

        // A Tray Start Shows No Panel, So Say It Here
        if (
            !_shell.ApplyHotkey(Hotkey.Parse(_controller.Settings.Hotkey) ?? Hotkey.Default)
            && _shell.HotkeyProblem is { } problem
        )
        {
            _trayIcon.ShowBalloonTip(
                BalloonMilliseconds,
                "Save Hotkey Taken",
                problem,
                Forms.ToolTipIcon.Warning
            );
        }
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

        // Capture Stops And A Running Save Finishes
        _isExiting = true;
        Hide();
        await _controller.HoldAsync(CaptureHolds.Exiting, "Stopped because LetsClip is closing");
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
        var outcome = await Task.Run(_controller.SaveAsync);
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
        TrayBalloon.ShowQuiet(
            _trayIcon,
            BalloonMilliseconds,
            "Clip Saved",
            $"{Formatter.FormatDuration(clip.Duration)} clip, click to open"
        );
    }

    private void PlaySaveChime()
    {
        // A Soft Chime At The Press, Muted Out Of Clips
        if (_controller.Settings.SaveSound)
        {
            SaveChime.Play(_controller.Recorder);
        }
    }

    private void OpenLastClip()
    {
        ShowFromTray();
        if (_lastSavedClip is not { } path)
        {
            return;
        }

        // A Clip Saved Elsewhere Shows In Explorer Instead
        if (FilePaths.AreSame(Path.GetDirectoryName(path), _controller.Library.Folder))
        {
            _shell.RequestOpenClip(path);
        }
        else
        {
            Explorer.ShowFile(path);
        }
    }

    private void ShowTrayMenu()
    {
        var seconds = _controller.Settings.ReplaySeconds;
        var open = new TrayMenuItem("Open LetsClip");
        var save = new TrayMenuItem($"Save Last {seconds} Seconds", _controller.IsRecording);
        var exit = new TrayMenuItem("Exit");
        var picked = TrayMenu.Show(
            new WindowInteropHelper(this).EnsureHandle(),
            open,
            save,
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
        else if (picked == exit)
        {
            _ = ExitAsync();
        }
    }

    private void UpdateTray()
    {
        // The Same Words As The Chip
        var status = _controller.Recorder.Status;
        _trayIcon.Icon =
            status.State == CaptureState.Error ? _problemIcon
            : _controller.IsRecording ? _recordingIcon
            : _plainIcon;
        var text =
            status.State == CaptureState.Error
                ? $"{AppInfo.DisplayName}, Error, {status.Message}"
                : $"{AppInfo.DisplayName}, {_controller.StatusText}";
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
        browser.ContainsFullScreenElementChanged += (_, _) =>
            ShowFullScreen(browser.ContainsFullScreenElement);

        Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
        browser.SetVirtualHostNameToFolderMapping(
            DesktopShell.ThumbnailsHost,
            AppInfo.ThumbnailsDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors
        );
        MapClipsFolder();
    }

    private void ShowFullScreen(bool isFullScreen)
    {
        // Player Full Screen Takes The Whole Monitor
        if (isFullScreen)
        {
            _windowedState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            return;
        }

        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = _windowedState;
    }

    private void MapClipsFolder()
    {
        // Only The Clips Folder Is Reachable, Streamed With Range Requests
        var folder = _controller.Settings.ClipsDirectory;
        if (_browser is null || FilePaths.AreSame(folder, _mappedClipsFolder))
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
                    "It keeps recording in the tray, right click the icon to exit",
                    Forms.ToolTipIcon.Info
                );
            }
        }

        base.OnClosing(eventArgs);
    }
}
