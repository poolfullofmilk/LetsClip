using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using Microsoft.Win32;
using NLog;

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
        OpenClipRequested?.Invoke(path);
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
            var overlay = new MonitorOverlay(monitor, monitor.DevicePath == chosen?.DevicePath);
            overlay.Show();
            _overlays.Add(overlay);
        }
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
    public static string ClipUrl(string fileName)
    {
        // A Checked File Name The Clips Host Serves
        return $"https://{ClipsHost}/{Uri.EscapeDataString(fileName)}";
    }

    public static string ThumbnailUrl(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return "";
        }

        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        return $"https://{ThumbnailsHost}/{Uri.EscapeDataString(Path.GetFileName(path))}?v={stamp}";
    }

    public static bool CopyFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // The File Itself Like Explorer's Copy, Pasting Copies It
        var data = new DataObject();
        data.SetFileDropList([path]);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (COMException)
            {
                // Another App Holds The Clipboard For A Moment
                Thread.Sleep(50);
            }
        }

        return false;
    }

    public static void DragFile(string path)
    {
        if (!File.Exists(path) || Application.Current.MainWindow is not { } window)
        {
            return;
        }

        var data = new DataObject();
        data.SetFileDropList([path]);
        DragDrop.DoDragDrop(window, data, DragDropEffects.Copy);
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
            Title = "Choose a Game to Record",
            Filter = "Games|*.exe",
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    #endregion
}
