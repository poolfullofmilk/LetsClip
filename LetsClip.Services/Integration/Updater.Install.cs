using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using LetsClip.Data.Helpers;

namespace LetsClip.Services.Integration;

public static partial class Updater
{
    // A Restart Waits For The Crashed Copy, Like An Update
    public static string RestartArguments =>
        $"{AfterUpdateArgument} {Environment.ProcessId} \"{Environment.ProcessPath}\" --tray";

    public static bool MoveToInstallFolder(string[] arguments)
    {
        // Every Release Runs As Programs\LetsClip\LetsClip.exe
        var running = Environment.ProcessPath!;
        if (!CanInstall || FilePaths.AreSame(running, AppInfo.InstallPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(AppInfo.InstallDirectory);

            // A Newer Download Takes Over From A Running Install
            if (RunningInstall() is { } occupant)
            {
                using (occupant)
                {
                    return TakeOver(occupant, running, arguments);
                }
            }

            File.Copy(running, AppInfo.InstallPath, overwrite: true);
            Start(AppInfo.InstallPath, [.. arguments, InstalledFromArgument, running]);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Unwritable Or Blocked, It Runs From Where It Is
            return false;
        }
    }

    private static bool TakeOver(Process occupant, string running, string[] arguments)
    {
        // The Same Or A Newer Install Just Shows Its Window
        var installed = VersionOf(AppInfo.InstallPath);
        if (installed >= AppInfo.Version)
        {
            return false;
        }

        // Set Aside Until The New One Starts, Asked To Exit
        var aside = AsidePath(installed);
        File.Move(AppInfo.InstallPath, aside, overwrite: true);
        try
        {
            File.Copy(running, AppInfo.InstallPath);
        }
        catch
        {
            File.Move(aside, AppInfo.InstallPath);
            throw;
        }

        Signal(ExitSignal);
        Start(
            AppInfo.InstallPath,
            [
                .. arguments,
                AfterUpdateArgument,
                occupant.Id.ToString(CultureInfo.InvariantCulture),
                aside,
                InstalledFromArgument,
                running,
            ]
        );
        return true;
    }

    private static Process? RunningInstall()
    {
        // Another LetsClip Running From The Install Path
        var name = Path.GetFileNameWithoutExtension(AppInfo.InstallPath);
        Process? found = null;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (
                    found is null
                    && process.Id != Environment.ProcessId
                    && FilePaths.AreSame(process.MainModule?.FileName, AppInfo.InstallPath)
                )
                {
                    found = process;
                    continue;
                }
            }
            catch (Exception exception)
                when (exception is Win32Exception or InvalidOperationException)
            {
                // Gone Or Elevated, Not Ours To Replace
            }

            process.Dispose();
        }

        return found;
    }

    private static Version VersionOf(string path)
    {
        return Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var version)
            ? version
            : new Version(0, 0);
    }

    private static string AsidePath(Version version)
    {
        // Named Like Its Release, So Every Cleanup Knows It
        return Path.Combine(
            AppInfo.InstallDirectory,
            $"{ExecutablePrefix}{version.ToString(version.Build > 0 ? 3 : 2)}.exe"
        );
    }

    private static void Signal(string signal)
    {
        // The Running LetsClip Listens, Nothing Happens If None Runs
        if (EventWaitHandle.TryOpenExisting(AppInfo.SignalName(signal), out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }

    private static void Start(string executablePath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo.WithoutWebView())?.Dispose();
    }

    public static string? DownloadOf(string[] arguments)
    {
        // Set Only By The Copy That Just Installed This One
        var index = Array.IndexOf(arguments, InstalledFromArgument);
        return AppInfo.SandboxRoot is null && index >= 0 && index + 1 < arguments.Length
            ? arguments[index + 1]
            : null;
    }

    public static void DeleteDownload(string download)
    {
        // Only A Versioned LetsClip Exe, Never The One Running
        if (!IsVersioned(download) || FilePaths.AreSame(download, Environment.ProcessPath))
        {
            return;
        }

        for (var attempt = 1; File.Exists(download); attempt++)
        {
            try
            {
                File.Delete(download);
                s_logger.Info($"Deleted The Download {download}");
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // Still Running Or Locked, In The End It Stays
                if (attempt == DownloadDeleteAttempts)
                {
                    s_logger.Info($"Kept The Download {exception.GetType().Name}");
                    return;
                }

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    public static void Launch(string executablePath, bool inTray)
    {
        // The Installed Exe Steps Aside Until The New One Starts
        var previous = Environment.ProcessPath!;
        var target = executablePath;
        if (
            FilePaths.AreSame(previous, AppInfo.InstallPath)
            && !FilePaths.AreSame(executablePath, AppInfo.InstallPath)
        )
        {
            var aside = AsidePath(AppInfo.Version);
            try
            {
                File.Move(previous, aside, overwrite: true);
                try
                {
                    File.Move(executablePath, AppInfo.InstallPath);
                }
                catch
                {
                    File.Move(aside, previous);
                    throw;
                }

                (previous, target) = (aside, AppInfo.InstallPath);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // The New Exe Takes The Name Once This One Exits
                s_logger.Warn($"Failed To Swap The Installed Exe {exception.GetType().Name}");
            }
        }

        // An Update While Hidden Stays In The Tray
        s_logger.Info($"Started Update {target}{(inTray ? " In The Tray" : "")}");
        Start(
            target,
            [
                AfterUpdateArgument,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                previous,
                .. inTray ? ["--tray"] : Array.Empty<string>(),
            ]
        );
    }

    public static string? WaitForPreviousVersion(string[] arguments)
    {
        var index = Array.IndexOf(arguments, AfterUpdateArgument);
        if (
            AppInfo.SandboxRoot != null
            || index < 0
            || index + 1 >= arguments.Length
            || !int.TryParse(arguments[index + 1], out var processId)
        )
        {
            return null;
        }

        // Newer Versions Name Themselves, Older Ones Are Asked
        var previousPath = index + 2 < arguments.Length ? arguments[index + 2] : null;
        try
        {
            using var previous = Process.GetProcessById(processId);
            previousPath ??= previous.MainModule?.FileName;

            // Still Running After The Wait, Its Exe Stays
            if (!previous.WaitForExit(s_previousExitWait))
            {
                s_logger.Warn($"Found Previous Version {processId} Still Running");
                return null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception)
        {
            // Already Gone, Or It Would Not Say Where It Lives
        }

        return previousPath;
    }

    public static void DeleteOtherVersions()
    {
        // Started Well As The Installed Exe, Older Ones Go
        if (
            AppInfo.SandboxRoot is not null
            || !FilePaths.AreSame(Environment.ProcessPath, AppInfo.InstallPath)
        )
        {
            return;
        }

        foreach (
            var older in Directory.EnumerateFiles(
                AppInfo.InstallDirectory,
                ExecutablePrefix + "*.exe"
            )
        )
        {
            try
            {
                File.Delete(older);
                s_logger.Info($"Deleted The Older Version {Path.GetFileName(older)}");
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A Running Or Locked Old Exe Goes Next Time
                s_logger.Warn($"Failed To Delete An Older Version {exception.GetType().Name}");
            }
        }
    }

    public static void DeleteOldUnpackedVersions()
    {
        // Each Version Unpacks Into %Temp%\.net, Never Removed By Windows
        var current = new DirectoryInfo(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        );
        if (
            AppInfo.SandboxRoot is not null
            || current.Parent is not { } version
            || !IsUnpackedName(version.Name)
            || version.Parent is not { Name: ".net" } root
        )
        {
            return;
        }

        var unpackedFolders = root.EnumerateDirectories()
            .Where(folder => IsUnpackedName(folder.Name))
            .SelectMany(folder => folder.EnumerateDirectories())
            .Where(unpacked => !FilePaths.AreSame(unpacked.FullName, current.FullName))
            .ToList();
        foreach (var unpacked in unpackedFolders)
        {
            DeleteUnpacked(unpacked);
        }
    }

    private static bool IsUnpackedName(string name)
    {
        // Named After The Exe, LetsClip Or An Older LetsClip_v Name
        return name.Equals(AppInfo.DisplayName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteUnpacked(DirectoryInfo unpacked)
    {
        var version = unpacked.Parent!;
        try
        {
            // Open Files Block A Rename, So A Running Copy Stays
            if (!unpacked.Name.EndsWith("-old", StringComparison.Ordinal))
            {
                unpacked.MoveTo(unpacked.FullName + "-old");
            }

            unpacked.Delete(recursive: true);
            if (!version.EnumerateFileSystemInfos().Any())
            {
                version.Delete();
            }

            s_logger.Info($"Deleted The Unpacked {version.Name}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // In Use Or Locked, The Next Start Tries Again
            s_logger.Debug($"Kept The Unpacked {version.Name}, {exception.GetType().Name}");
        }
    }
}
