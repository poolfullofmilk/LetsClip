using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components;
using LetsClip.Desktop.Components.Pages;
using LetsClip.Desktop.Interop;
using LetsClip.Desktop.Theming;
using LetsClip.Services.Capture;
using LetsClip.Services.Games;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Web.WebView2.Core;
using MudBlazor.Utilities;
using NLog;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LetsClip.Desktop;

public partial class MainWindow : Window
{
    private const int BalloonMilliseconds = 4000;

    // The Smallest Window The Page Is Laid Out For
    private const double PageWidth = 1280;
    private const double PageHeight = 540;

    // A Quick Hide And Show Never Suspends
    private static readonly TimeSpan s_suspendDelay = TimeSpan.FromSeconds(1);

    // Longer In The Tray, The Page Frees WebView2's 175 MB
    private static readonly TimeSpan s_releaseDelay = TimeSpan.FromMinutes(10);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly IServiceProvider _services;
    private readonly CaptureController _controller;
    private readonly DesktopShell _shell;
    private readonly Forms.NotifyIcon _trayIcon;

    // Tray Icons Per State, Drawn Once
    private readonly Drawing.Icon _plainIcon;
    private readonly Drawing.Icon _recordingIcon;
    private readonly Drawing.Icon _problemIcon;

    private WindowMessages? _messages;
    private BlazorWebView? _page;
    private CoreWebView2? _browser;

    // Each Show Or Hide Counts, A Later Hide Waits Anew
    private int _visibility;
    private string? _mappedClipsFolder;

    // What A Click On The Latest Balloon Does
    private Action? _balloonClick;
    private bool _isExiting;
    private WindowState _windowedState;
    private double _pageZoom = 1;

    public MainWindow(IServiceProvider services, CaptureController controller, DesktopShell shell)
    {
        InitializeComponent();
        _services = services;
        _controller = controller;
        _shell = shell;
        SourceInitialized += (_, _) =>
        {
            WindowCaptionTheme.Apply(this);
            FitToScreen();
        };
        DpiChanged += (_, _) => FitToScreen();
        LocationChanged += (_, _) => FitToScreen();
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
        _trayIcon.BalloonTipClicked += (_, _) => (_balloonClick ?? ShowFromTray)();

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
            !_shell.ApplyHotkey(Hotkey.Parse(_controller.Settings.Hotkey))
            && _shell.HotkeyProblem is { } problem
        )
        {
            ShowBalloon("Save Hotkey Taken", problem, Forms.ToolTipIcon.Warning);
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

        await Dispatcher.InvokeAsync(() =>
        {
            if (front is { } app && _controller.Games.GameName is null)
            {
                ShowBalloon(
                    "Clip Not Saved",
                    $"Nothing is being recorded, click to record {app.Name} from now on",
                    Forms.ToolTipIcon.Warning,
                    () => RecordFromNowOn(app)
                );
                return;
            }

            ShowBalloon("Clip Not Saved", error, Forms.ToolTipIcon.Warning);
        });
    }

    private void RecordFromNowOn(AddedGame app)
    {
        // One Click Turns A Missed Clip Into A Recorded Game
        _controller.SetRecorded([app], true);
        _balloonClick = null;
        TrayBalloon.ShowQuiet(
            _trayIcon,
            BalloonMilliseconds,
            "Added to Games and Apps",
            $"{app.Name} is recorded from now on"
        );
    }

    private void ShowBalloon(
        string title,
        string text,
        Forms.ToolTipIcon icon,
        Action? onClick = null
    )
    {
        _balloonClick = onClick;
        _trayIcon.ShowBalloonTip(BalloonMilliseconds, title, text, icon);
    }

    private void NotifySaved(ClipInfo clip)
    {
        _balloonClick = () => OpenClip(clip.Path);
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
            SaveChime.Play(_controller.Recorder, _controller.Settings.SaveSoundVolume);
        }
    }

    private void OpenClip(string path)
    {
        // A Clip Saved Elsewhere Shows In Explorer Instead
        if (!FilePaths.AreSame(Path.GetDirectoryName(path), _controller.Library.Folder))
        {
            ShowFromTray();
            Explorer.ShowFile(path);
            return;
        }

        // A Page Not Built Yet Starts On The Clip
        if (_page is null)
        {
            EnsurePage(Clips.EditorOf(path));
        }
        else
        {
            _shell.RequestOpenClip(path);
        }

        ShowFromTray();
    }

    private void EnsurePage(string startPath = "/clips")
    {
        // Built On The First Show, And Again After A Release
        if (_page is not null)
        {
            return;
        }

        var page = new BlazorWebView
        {
            HostPage = @"wwwroot\index.html",
            StartPath = startPath,
            Services = _services,
        };
        page.RootComponents.Add(
            new RootComponent { Selector = "#app", ComponentType = typeof(Routes) }
        );
        page.BlazorWebViewInitialized += (_, eventArgs) =>
        {
            if (page == _page)
            {
                SetUpBrowser(page, eventArgs.WebView.CoreWebView2);
            }
        };
        _page = page;
        Content = page;
    }

    private async Task ReleasePageAsync()
    {
        if (_page is not { } page)
        {
            return;
        }

        // A Hidden Full Screen Player Comes Back As A Window
        if (WindowStyle == WindowStyle.None)
        {
            ShowFullScreen(false);
        }

        // A Suspended Page Cannot Answer Its Components' Last Calls
        if (_browser is { IsSuspended: true } browser)
        {
            browser.Resume();
        }

        _page = null;
        _browser = null;
        _mappedClipsFolder = null;
        var released = Stopwatch.StartNew();
        try
        {
            await page.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Components Close At Once, Then The Hidden Host Waits
            page.WebView?.Dispose();
            s_logger.Debug("Disposed WebView2 Directly, Its Host Waited On The Hidden Window");
        }

        Content = null;

        s_logger.Info(
            $"Released The Page After 10 Minutes In The Tray In {released.ElapsedMilliseconds} ms"
        );
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
            _controller.ShowsError ? _problemIcon
            : _controller.IsRecording ? _recordingIcon
            : _plainIcon;
        var text = _controller.ShowsError
            ? $"{AppInfo.DisplayName}, Error, {status.Message}"
            : $"{AppInfo.DisplayName}, {_controller.StatusText}";
        _trayIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void FitToScreen()
    {
        // Small Or Heavily Scaled Screens Still Show The Whole Page
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var (width, height) = (area.Width / scale, area.Height / scale);
        (MinWidth, MinHeight) = (Math.Min(PageWidth, width), Math.Min(PageHeight, height));
        (Width, Height) = (Math.Min(Width, width), Math.Min(Height, height));
        _pageZoom = Math.Min(1, width / PageWidth);
        if (_page?.WebView is { CoreWebView2: not null } control)
        {
            control.ZoomFactor = _pageZoom;
        }
    }

    private void SetUpBrowser(BlazorWebView page, CoreWebView2 browser)
    {
        _browser = browser;

        // Page Background Before The First Paint, No White Flash
        page.WebView.DefaultBackgroundColor = Drawing.Color.FromArgb(0x20, 0x20, 0x20);
        page.WebView.ZoomFactor = _pageZoom;

        // A Crashed Page Reloads Itself, Recording Never Depended On It
        browser.ProcessFailed += (_, eventArgs) =>
        {
            s_logger.Warn($"Browser Failed, {eventArgs.ProcessFailedKind}");
            if (
                eventArgs.ProcessFailedKind
                is CoreWebView2ProcessFailedKind.RenderProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            )
            {
                browser.Reload();
            }
        };

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
        var visibility = ++_visibility;
        if (IsVisible)
        {
            EnsurePage();
        }

        if (_page?.WebView is not { CoreWebView2: { } browser } control)
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
            return;
        }

        // Still Hidden Later, The Page Goes, Never During An Export
        await Task.Delay(s_releaseDelay);
        while (visibility == _visibility && ClipExporter.IsExporting)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
        }

        if (visibility == _visibility)
        {
            await ReleasePageAsync();
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
        }

        base.OnClosing(eventArgs);
    }
}
