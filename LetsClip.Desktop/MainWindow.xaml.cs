using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Interop;
using LetsClip.Data.Helpers;
using LetsClip.Desktop.Components;
using LetsClip.Desktop.Components.Pages;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Web.WebView2.Core;
using NLog;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LetsClip.Desktop;

public partial class MainWindow : Window
{
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

    private BlazorWebView? _page;
    private CoreWebView2? _browser;

    // Each Show Or Hide Counts, A Later Hide Waits Anew
    private int _visibility;
    private string? _mappedClipsFolder;
    private WindowState _windowedState;
    private Size _windowedSize;
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
        controller.Changed += () => Dispatcher.InvokeAsync(MapClipsFolder);
    }

    // Only The Tray's Exit Ends LetsClip, Closing Hides
    public bool IsExiting { get; set; }

    public TimeSpan? HiddenFor =>
        _hiddenSince is { } hidden ? Stopwatch.GetElapsedTime(hidden) : null;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void OpenClip(string path)
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

            // The Plain Control, The Composition One Copies Every Frame
            UseCompositionControl = false,
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
        if (!browserGone)
        {
            // The Page's Heap Goes Back Now, Measured 50 MB
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        }

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

    private void FitToScreen()
    {
        // Small Or Heavily Scaled Screens Still Show The Whole Page
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var (width, height) = (area.Width / scale, area.Height / scale);
        (MinWidth, MinHeight) = (Math.Min(PageWidth, width), Math.Min(PageHeight, height));
        (Width, Height) = (Math.Min(Width, width), Math.Min(Height, height));

        // A Window Drag Asks Often, The Zoom Rarely Differs
        var zoom = Math.Min(1, width / PageWidth);
        if (zoom == _pageZoom)
        {
            return;
        }

        _pageZoom = zoom;
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
            _windowedSize = new Size(Width, Height);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            return;
        }

        // The Size It Had, Full Screen Left It Screen Sized
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = _windowedState;
        (Width, Height) = (_windowedSize.Width, _windowedSize.Height);
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

    private async Task RestPageAsync()
    {
        var visibility = ++_visibility;
        _hiddenSince = IsVisible ? null : Stopwatch.GetTimestamp();
        if (IsVisible)
        {
            EnsurePage();
        }

        if (_page?.WebView is not UIElement { } control || _browser is not { } browser)
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
        if (!IsExiting)
        {
            eventArgs.Cancel = true;
            Hide();
        }

        base.OnClosing(eventArgs);
    }
}
