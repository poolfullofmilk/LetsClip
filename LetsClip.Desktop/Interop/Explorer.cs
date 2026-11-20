using System.ComponentModel;
using System.Diagnostics;
using LetsClip.Data.Helpers;
using LetsClip.Services.Media;
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

    public static void Open(string target)
    {
        // A File Or Link In Its Default App, Through Explorer
        Start(new ProcessStartInfo("explorer.exe", $"\"{target}\""));
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\""));
    }

    public static bool Recycle(string path)
    {
        // To The Recycle Bin, So A Mistake Comes Back
        var original = ClipLibrary.OwnedOriginalOf(path);
        if (!ClipLibrary.Recycle(path))
        {
            return false;
        }

        // Its Hidden Original Follows, The Clip Is Gone Either Way
        if (original is not null)
        {
            _ = ClipLibrary.Recycle(original);
        }

        return true;
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo.WithoutWebView())?.Dispose();
        }
        catch (Win32Exception exception)
        {
            // A Failed Shell Launch Never Fails What Asked
            s_logger.Warn($"Failed To Open Explorer {exception.GetType().Name}");
        }
    }
}
