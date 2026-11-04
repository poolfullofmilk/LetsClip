using System.ComponentModel;
using System.Diagnostics;
using NLog;

namespace LetsClip.Desktop.Interop;

public static class Explorer
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    public static void ShowFile(string path)
    {
        // A File Gone Since Loading Opens Its Folder Instead
        if (File.Exists(path))
        {
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
        {
            OpenFolder(folder);
        }
    }

    public static void OpenFile(string path)
    {
        if (File.Exists(path))
        {
            Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\""));
    }

    public static void OpenLink(string url)
    {
        Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Win32Exception exception)
        {
            // A Failed Shell Launch Never Fails What Asked
            s_logger.Warn($"Failed To Open Explorer {exception.GetType().Name}");
        }
    }
}
