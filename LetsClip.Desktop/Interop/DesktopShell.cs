using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using Microsoft.Win32;
using NLog;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LetsClip.Desktop.Interop;

public sealed class DesktopShell
{
    // Virtual Hosts For Local Media, .example Never Reaches The Web
    public const string ClipsHost = "clips.letsclip.example";
    public const string ThumbnailsHost = "thumbs.letsclip.example";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // A Picked Monitor Stays Outlined This Long
    private static readonly TimeSpan s_flashTime = TimeSpan.FromSeconds(1.5);

    private readonly List<MonitorOverlay> _overlays = [];
    private MonitorOverlay? _flash;
    private DispatcherTimer? _flashTimer;
    private WindowMessages? _messages;
    private string? _pendingClip;

    public event Action<string>? OpenClipRequested;

    public event Action? HotkeyChanged;

    public string? HotkeyProblem { get; private set; }

    public string HotkeyText { get; private set; } = Hotkey.Default.ToString();

    public void Attach(WindowMessages messages)
    {
        _messages = messages;
    }

    public void RequestOpenClip(string path)
    {
        // A Page Still Loading Opens It Once It Listens
        if (OpenClipRequested is null)
        {
            _pendingClip = path;
            return;
        }

        OpenClipRequested(path);
    }

    public string? TakePendingClip()
    {
        return Interlocked.Exchange(ref _pendingClip, null);
    }

    #region Hotkey
    public bool ApplyHotkey(Hotkey hotkey)
    {
        HotkeyText = hotkey.ToString();
        var registered = _messages?.RegisterHotkey(hotkey) ?? false;

        // Another App Holding The Keys Is Said Plainly
        HotkeyProblem = registered
            ? null
            : $"{HotkeyText} is taken by another app, pick other keys";
        if (!registered)
        {
            s_logger.Warn($"Failed To Register Hotkey {HotkeyText}");
        }

        HotkeyChanged?.Invoke();
        return registered;
    }

    public void PauseHotkey()
    {
        // New Keys Pressed In Settings Must Reach The Page
        _messages?.UnregisterHotkey();
    }
    #endregion

    #region Monitors
    public void ShowMonitors(IEnumerable<MonitorInfo> monitors, MonitorInfo? chosen)
    {
        // While The List Is Open, Every Monitor Shows Its Name
        HideMonitors();
        CloseFlash();
        foreach (var monitor in monitors)
        {
            // Drawn Unseen, So No Monitor Lights Up Before Another
            var overlay = new MonitorOverlay(monitor, monitor.DevicePath == chosen?.DevicePath)
            {
                Opacity = 0,
            };
            overlay.Show();
            _overlays.Add(overlay);
        }

        List<MonitorOverlay> drawn = [.. _overlays];
        _ = Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                foreach (var overlay in drawn.Where(_overlays.Contains))
                {
                    overlay.Opacity = 1;
                }
            }
        );
    }

    public void HideMonitors()
    {
        foreach (var overlay in _overlays)
        {
            overlay.Close();
        }

        _overlays.Clear();
    }

    public void FlashMonitor(MonitorInfo monitor)
    {
        // The Pick Stays Outlined A Moment After The List Closes
        HideMonitors();
        CloseFlash();
        _flash = new MonitorOverlay(monitor, true);
        _flash.Show();
        if (_flashTimer is null)
        {
            _flashTimer = new DispatcherTimer { Interval = s_flashTime };
            _flashTimer.Tick += (_, _) => CloseFlash();
        }

        _flashTimer.Start();
    }

    private void CloseFlash()
    {
        _flashTimer?.Stop();
        _flash?.Close();
        _flash = null;
    }
    #endregion

    #region Files
    public static string ClipUrl(string path)
    {
        return UrlOf(ClipsHost, path);
    }

    public static string ThumbnailUrl(string? path)
    {
        return path is not null && File.Exists(path) ? UrlOf(ThumbnailsHost, path) : "";
    }

    private static string UrlOf(string host, string path)
    {
        // Stamped, So A Changed File Never Comes From Cache
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        return $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(path))}?v={stamp}";
    }

    public static string IconUrl(string? executablePath)
    {
        if (executablePath is null || !File.Exists(executablePath))
        {
            return "";
        }

        var name = $"icon-{FilePaths.KeyOf(executablePath)}.png";
        var file = Path.Combine(AppInfo.ThumbnailsDirectory, name);
        try
        {
            if (!File.Exists(file))
            {
                // 48 Px Stays Sharp At 24 Px Up To 200%
                using var icon = Drawing.Icon.ExtractIcon(executablePath, 0, 48);
                if (icon is null)
                {
                    return "";
                }

                using var bitmap = icon.ToBitmap();
                bitmap.Save(file, Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or ExternalException
                        or ArgumentException
            )
        {
            // An Exe Without A Readable Icon Shows The Plain One
            return "";
        }

        return $"https://{ThumbnailsHost}/{name}";
    }

    public static bool CopyFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // The File Itself Like Explorer's Copy, Pasting Copies It
        var data = new Forms.DataObject();
        data.SetFileDropList([path]);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        try
        {
            // Five Tries 50 ms Apart, Another App May Hold It
            Forms.Clipboard.SetDataObject(data, true, 5, 50);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public static string? PickFolder(string current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Where LetsClip Saves Clips",
            InitialDirectory = Directory.Exists(current) ? current : null,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public static string? PickGame()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Game or App to Record",
            Filter = "Apps|*.exe",
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    #endregion
}
