using System.ComponentModel;
using System.Diagnostics;
using LetsClip.Data.Helpers;
using LetsClip.Services.Media;
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
        if (!TryRecycle(path))
        {
            return false;
        }

        // Its Hidden Original Follows, The Clip Is Gone Either Way
        if (original is not null)
        {
            _ = TryRecycle(original);
        }

        return true;
    }

    private static bool TryRecycle(string file)
    {
        try
        {
            FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            s_logger.Info($"Moved {Path.GetFileName(file)} To The Recycle Bin");
            return true;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or OperationCanceledException
            )
        {
            s_logger.Warn($"Failed To Recycle {Path.GetFileName(file)} {exception.GetType().Name}");
            return false;
        }
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
