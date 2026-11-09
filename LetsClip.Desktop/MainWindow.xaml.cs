using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
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
using LetsClip.Services.Integration;
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

    // Looked For This Often, And Whenever The Window Opens
    private static readonly TimeSpan s_updateCheckPeriod = TimeSpan.FromHours(6);

    // Hidden This Long Is Idle, A Found Update Installs
    private static readonly TimeSpan s_idleBeforeUpdate = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_idleCheckPeriod = TimeSpan.FromMinutes(1);
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
    private bool _isUpdating;
    private WindowState _windowedState;
    private double _pageZoom = 1;

    // A Tray Start Counts As Hidden From Launch
    private long? _hiddenSince = Stopwatch.GetTimestamp();

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
        StateChanged += (_, _) =>
        {
            // Minimising Goes Straight To The Tray, Capture Keeps Running
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        };
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
        controller.Recorder.SaveStarted += () =>
        {
            // A Soft Chime At The Press, Muted Out Of Clips
            if (_controller.Settings.SaveSound)
            {
                SaveChime.Play(_controller.Recorder, _controller.Settings.SaveSoundVolume);
            }
        };
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
        _ = KeepUpdatedAsync();

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

    public async Task InstallUpdateAsync(
        ReleaseInfo release,
        IProgress<(long Received, long Total)>? progress
    )
    {
        // One At A Time, The New Exe Waits For Us
        if (_isUpdating || _isExiting)
        {
            return;
        }

        _isUpdating = true;
        try
        {
            var path = await Task.Run(() => Updater.DownloadAsync(release, progress));
            Updater.Launch(path, inTray: !IsVisible);
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
        while (!_isExiting)
        {
            if (await FindUpdateAsync() is { } release)
            {
                await InstallWhenIdleAsync(release);
            }

            await Task.Delay(s_updateCheckPeriod);
        }
    }

    private async Task<ReleaseInfo?> FindUpdateAsync()
    {
        // A Newer Release Gets Its Dialog And The Red Dot
        var latest = await Updater.CheckAsync();
        if (latest is null || latest.Version <= AppInfo.Version)
        {
            return null;
        }

        _shell.ReportUpdate(latest);
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

        // Hidden A While, Nothing Saving Or Exporting
        while (!_isExiting)
        {
            if (
                _hiddenSince is { } hidden
                && Stopwatch.GetElapsedTime(hidden) is var idle
                && idle >= s_idleBeforeUpdate
                && !ClipExporter.IsExporting
                && !_controller.Recorder.Status.IsSaving
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

    private async Task ReleasePageAsync(bool browserGone = false)
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
        if (!browserGone && _browser is { IsSuspended: true } browser)
        {
            browser.Resume();
        }

        _page = null;
        _browser = null;
        _mappedClipsFolder = null;
        var released = Stopwatch.StartNew();
        try
        {
            // A Dead Browser Never Answers, So Nothing Waits For It
            await page.DisposeAsync()
                .AsTask()
                .WaitAsync(browserGone ? TimeSpan.Zero : TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Components Close At Once, Then The Host Waits
            page.WebView?.Dispose();
            s_logger.Debug("Disposed WebView2 Directly, Its Host Was Still Waiting");
        }
        catch (Exception exception) when (browserGone)
        {
            // A Dead Browser May Fail Teardown, The Page Still Goes
            s_logger.Debug($"Disposed A Dead Page With {exception.GetType().Name}");
        }

        Content = null;

        s_logger.Info(
            $"Released The Page {(browserGone ? "After Its Browser Exited" : "After 10 Minutes In The Tray")} In {released.ElapsedMilliseconds} ms"
        );
    }

    private async Task RebuildPageAsync(BlazorWebView page)
    {
        // Rebuilt Now While Shown, Else When Next Shown
        if (page != _page)
        {
            return;
        }

        await ReleasePageAsync(browserGone: true);
        if (IsVisible)
        {
            EnsurePage();
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
            else if (
                eventArgs.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited
            )
            {
                // The Whole Browser Died, A New Page Replaces It
                _ = RebuildPageAsync(page);
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

    private async Task RestPageAsync()
    {
        var visibility = ++_visibility;
        _hiddenSince = IsVisible ? null : Stopwatch.GetTimestamp();
        if (IsVisible)
        {
            EnsurePage();
            _ = FindUpdateAsync();
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
