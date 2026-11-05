using System.ComponentModel;
using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
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
        // A File Or A Link In Its Default App
        Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\""));
    }

    public static bool Recycle(string path)
    {
        // To The Recycle Bin, So A Mistake Comes Back
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or OperationCanceledException
            )
        {
            s_logger.Warn($"Failed To Delete Clip {exception.GetType().Name}");
            return false;
        }
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
