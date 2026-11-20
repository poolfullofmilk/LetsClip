using System.ComponentModel;
using System.Diagnostics;
using LetsClip.Data.Helpers;

namespace LetsClip.Services.Integration;

public static partial class Updater
{
    public static void Uninstall()
    {
        // The Running LetsClip Exits First, So Its Files Let Go
        Signal(ExitSignal);
        WaitForOtherCopies();
        StartupRegistration.Remove();

        // Rebuilt By Any Start, Never Clips, Details Or Settings
        DeleteFolder(AppInfo.LocalDirectory);
        DeleteFolder(AppInfo.LogsDirectory);
        DeleteFolder(Path.GetDirectoryName(AppInfo.LogsDirectory)!, onlyEmpty: true);
        DeleteOldUnpackedVersions();
    }

    public static void DeleteInstallAfterExit()
    {
        // A Running Exe Cannot Delete Itself, Cmd Does It After
        if (AppInfo.SandboxRoot is not null)
        {
            return;
        }

        // The Install Folder, Browser Profile And Unpacked Files
        List<string> folders =
        [
            AppInfo.InstallDirectory,
            Path.Combine(AppInfo.DataDirectory, "WebView2"),
        ];
        var unpacked = new DirectoryInfo(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        ).Parent;
        if (unpacked is { Parent.Name: ".net" } && IsUnpackedName(unpacked.Name))
        {
            folders.Add(unpacked.FullName);
        }

        var removals = string.Join(" & ", folders.Select(folder => $"rmdir /s /q \"{folder}\""));
        // Cmd Reads Its Line Raw, Quoted Paths Only
        Process
            .Start(
                new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 > nul & {removals}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),
                }.WithoutWebView()
            )
            ?.Dispose();
    }

    private static void WaitForOtherCopies()
    {
        // Every Other LetsClip, Any Version, Gets Its Minute
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && IsUnpackedName(process.ProcessName))
                {
                    try
                    {
                        process.WaitForExit(s_previousExitWait);
                    }
                    catch (Exception exception)
                        when (exception is Win32Exception or InvalidOperationException)
                    {
                        // Gone Meanwhile
                    }
                }
            }
        }
    }

    private static void DeleteFolder(string folder, bool onlyEmpty = false)
    {
        try
        {
            if (
                Directory.Exists(folder)
                && (!onlyEmpty || !Directory.EnumerateFileSystemEntries(folder).Any())
            )
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A Locked Leftover Harms Nothing, The Clips Are Untouched
        }
    }
}
