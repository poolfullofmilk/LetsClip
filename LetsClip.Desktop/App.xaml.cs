using System.Security.Cryptography;
using System.Text;
using System.Windows;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MudBlazor;
using MudBlazor.Services;
using NLog;

namespace LetsClip.Desktop;

public partial class App : Application
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private EventWaitHandle? _showSignal;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        Updater.FinishPreviousVersion(eventArgs.Args);
        LogManager
            .Setup()
            .LoadConfiguration(configuration =>
                configuration
                    .ForLogger()
                    .FilterMinLevel(NLog.LogLevel.Debug)
                    // A Month Of Daily Logs, Older Ones Go
                    .WriteToFile(
                        Path.Combine(AppInfo.LogsDirectory, "log-${shortdate}.txt"),
                        maxArchiveFiles: 30
                    )
            );

        // A Second Launch Shows The First Window, Two Recorders Fight
        _showSignal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            AppInfo.SandboxRoot is { } sandbox
                ? $"{AppInfo.FolderName}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sandbox.ToUpperInvariant())))[..16]}.Show"
                : $"{AppInfo.FolderName}.Show",
            out var isFirstInstance
        );
        if (!isFirstInstance)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        try
        {
            Start(eventArgs.Args.Contains("--tray"));
        }
        catch (Exception exception)
        {
            // Without A Window The Process Would Linger Unseen
            s_logger.Fatal(exception, "Failed To Start");
            MessageBox.Show(
                $"{AppInfo.DisplayName} could not start, see the log in {AppInfo.LogsDirectory}",
                AppInfo.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Shutdown(1);
        }
    }

    private void Start(bool startsInTray)
    {
        if (!HasWebView())
        {
            MessageBox.Show(
                "LetsClip needs the Microsoft Edge WebView2 Runtime. Install it from Microsoft, then start LetsClip again",
                AppInfo.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(AppInfo.DataDirectory);
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(AppInfo.DataDirectory, "WebView2")
        );

        // Clips From The Tray Balloon Play With Sound Unclicked
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            $"{Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")} --autoplay-policy=no-user-gesture-required".Trim()
        );

        // A Crash Leaves Cache Behind, Cleared Off The Startup Path
        _ = Task.Run(CacheFolders.DeleteLeftovers);

        // A Failure In The Page Is Logged, Never Fatal
        DispatcherUnhandledException += (_, exceptionArgs) =>
        {
            exceptionArgs.Handled = true;
            s_logger.Error(exceptionArgs.Exception, "Failed In The Page");
        };
        TaskScheduler.UnobservedTaskException += (_, exceptionArgs) =>
        {
            s_logger.Error(exceptionArgs.Exception, "Failed In A Background Task");
            exceptionArgs.SetObserved();
        };

        // Every Log Starts With What Ran Where And Its Settings
        s_logger.Info(
            $"Started {AppInfo.DisplayName} v{AppInfo.VersionText}{(startsInTray ? " In The Tray" : "")} On {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}, {Environment.ProcessorCount} Threads{(AppInfo.SandboxRoot is null ? "" : ", Sandboxed")}"
        );
        var controller = new CaptureController();
        s_logger.Info($"Settings {System.Text.Json.JsonSerializer.Serialize(controller.Settings)}");
        StartupRegistration.Apply(controller.Settings.StartWithWindows);
        var shell = new DesktopShell();
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddMudServices(configuration =>
        {
            // Snackbar Defaults Are Not Rounded, LetsClip Uses Rounded
            configuration.SnackbarConfiguration.ShowCloseIcon = false;
            configuration.SnackbarConfiguration.SuccessIcon = Icons.Material.Rounded.CheckCircle;
            configuration.SnackbarConfiguration.WarningIcon = Icons.Material.Rounded.Warning;
            configuration.SnackbarConfiguration.InfoIcon = Icons.Material.Rounded.Info;
            configuration.SnackbarConfiguration.ErrorIcon = Icons.Material.Rounded.Error;
            configuration.SnackbarConfiguration.PositionClass = Defaults
                .Classes
                .Position
                .BottomRight;

            // Barely See-Through With A Quick Fade
            configuration.SnackbarConfiguration.MaximumOpacity = 92;
            configuration.SnackbarConfiguration.ShowTransitionDuration = 200;
            configuration.SnackbarConfiguration.HideTransitionDuration = 250;
        });

        // Tooltips Wait A Moment Before Showing
        MudGlobal.TooltipDefaults.Delay = TimeSpan.FromMilliseconds(300);
        services.AddSingleton(controller);
        services.AddSingleton(shell);

        _window = new MainWindow(services.BuildServiceProvider(), controller, shell);
        MainWindow = _window;
        _window.Attach();
        WatchSystem(controller);
        SessionEnding += (_, _) => _window.ExitNow();
        ThreadPool.RegisterWaitForSingleObject(
            _showSignal!,
            (_, _) => Dispatcher.InvokeAsync(_window.ShowFromTray),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false
        );

        // Signing In Starts LetsClip In The Tray, Already Recording
        if (!startsInTray)
        {
            _window.Show();
        }

        _ = controller.StartAutomaticallyAsync();
    }

    private static bool HasWebView()
    {
        try
        {
            return !string.IsNullOrEmpty(
                CoreWebView2Environment.GetAvailableBrowserVersionString()
            );
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    private static void WatchSystem(CaptureController controller)
    {
        // Lock And Sleep Clear The History, Unlock And Wake Restart
        SystemEvents.SessionSwitch += (_, eventArgs) =>
        {
            _ = eventArgs.Reason switch
            {
                SessionSwitchReason.SessionLock
                or SessionSwitchReason.ConsoleDisconnect
                or SessionSwitchReason.RemoteConnect
                or SessionSwitchReason.SessionLogoff => controller.HoldAsync(
                    CaptureHolds.Locked,
                    "Stopped because Windows was locked, history was cleared"
                ),
                SessionSwitchReason.SessionUnlock
                or SessionSwitchReason.ConsoleConnect
                or SessionSwitchReason.RemoteDisconnect => controller.ReleaseAsync(
                    CaptureHolds.Locked
                ),
                _ => Task.CompletedTask,
            };
        };
        SystemEvents.PowerModeChanged += (_, eventArgs) =>
        {
            _ = eventArgs.Mode switch
            {
                PowerModes.Suspend => controller.HoldAsync(
                    CaptureHolds.Asleep,
                    "Stopped because the PC went to sleep, history was cleared"
                ),
                PowerModes.Resume => controller.ReleaseAsync(CaptureHolds.Asleep),
                _ => Task.CompletedTask,
            };
        };
        // Any Change To The Recorded Monitor Restarts The Capture
        SystemEvents.DisplaySettingsChanged += (_, _) => _ = controller.DisplaysChangedAsync();
    }
}
