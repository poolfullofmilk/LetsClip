using System.Runtime.InteropServices;
using LetsClip.Data.Helpers;
using Microsoft.Win32;
using NLog;

namespace LetsClip.Services.Integration;

public static partial class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.DisplayName;
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // Updates And Reboots Already Start It, Only Crashes And Hangs
    private const int RestartNoPatch = 4;
    private const int RestartNoReboot = 8;

    // The Owner's 01 Apps Folder Where It Exists, Else Programs
    private static readonly string s_programs = Environment.GetFolderPath(
        Environment.SpecialFolder.Programs
    );
    private static readonly string s_shortcutPath = Path.Combine(
        Directory.Exists(Path.Combine(s_programs, "01 Apps"))
            ? Path.Combine(s_programs, "01 Apps")
            : s_programs,
        $"{AppInfo.DisplayName}.lnk"
    );

    // Named Without A Version, Made Once When Installed
    private static readonly string s_desktopShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        $"{AppInfo.DisplayName}.lnk"
    );

    // Debug Builds And Test Runs Never Register With Windows
#if DEBUG
    private static readonly bool s_registers = false;
#else
    private static readonly bool s_registers = AppInfo.SandboxRoot is null;
#endif

    public static void Apply(bool isEnabled, bool isInstall = false)
    {
        if (!s_registers)
        {
            return;
        }

        // Windows Brings A Crashed Or Hung LetsClip Back, In Tray
        _ = RegisterApplicationRestart(Updater.RestartArguments, RestartNoPatch | RestartNoReboot);
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);

            // Rewritten Every Launch, It Points Where This Exe Runs
            if (isEnabled)
            {
                runKey.SetValue(AppInfo.DisplayName, $"\"{Environment.ProcessPath}\" --tray");
            }
            else
            {
                runKey.DeleteValue(AppInfo.DisplayName, throwOnMissingValue: false);
            }

            WriteShortcut(s_shortcutPath);

            // Kept Current After, Never Brought Back Once Deleted
            if (isInstall || File.Exists(s_desktopShortcutPath))
            {
                WriteShortcut(s_desktopShortcutPath);
            }

            WriteUninstallEntry();
        }
        catch (Exception exception)
        {
            // A Locked Registry Must Not Stop Recording
            s_logger.Warn($"Failed To Register With Windows {exception.GetType().Name}");
        }
    }

    public static void Remove()
    {
        if (!s_registers)
        {
            return;
        }

        // Every Trace Windows Keeps, The Clips Stay
        try
        {
            using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                runKey.DeleteValue(AppInfo.DisplayName, throwOnMissingValue: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            File.Delete(s_shortcutPath);
            File.Delete(s_desktopShortcutPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Whatever Stays Is Harmless Without The Exe
            s_logger.Warn($"Failed To Unregister From Windows {exception.GetType().Name}");
        }
    }

    private static void WriteUninstallEntry()
    {
        // Listed In Settings, Apps, With Its Size And Uninstall
        var executable = Environment.ProcessPath!;
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", AppInfo.DisplayName);
        key.SetValue("DisplayVersion", AppInfo.VersionText.TrimStart('v'));
        key.SetValue("Publisher", AppInfo.Author);
        key.SetValue("DisplayIcon", executable);
        key.SetValue("InstallLocation", Path.GetDirectoryName(executable)!);
        key.SetValue("UninstallString", $"\"{executable}\" {Updater.UninstallArgument}");
        key.SetValue("URLInfoAbout", AppInfo.RepositoryUrl);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", SizeInKilobytes(executable), RegistryValueKind.DWord);
    }

    private static int SizeInKilobytes(string executable)
    {
        // The Exe And What It Unpacked, Both Are On Disk
        var unpacked = new DirectoryInfo(AppContext.BaseDirectory);
        var bytes =
            new FileInfo(executable).Length
            + unpacked.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        return (int)Math.Min(int.MaxValue, bytes / 1024);
    }

    private static void WriteShortcut(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // WScript Shell Writes Shortcuts Without A Package
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = Environment.ProcessPath;
        shortcut.Description = AppInfo.DisplayName;
        shortcut.Save();
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, int flags);
}
