using System.Runtime.InteropServices;
using LetsClip.Data.Helpers;
using Microsoft.Win32;
using NLog;

namespace LetsClip.Services.Integration;

public static partial class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
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

    // Debug Builds And Test Runs Never Register With Windows
#if DEBUG
    private static readonly bool s_registers = false;
#else
    private static readonly bool s_registers = AppInfo.SandboxRoot is null;
#endif

    public static void Apply(bool isEnabled)
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

            // Rewritten Every Launch, The Exe Name Carries The Version
            if (isEnabled)
            {
                runKey.SetValue(AppInfo.DisplayName, $"\"{Environment.ProcessPath}\" --tray");
            }
            else
            {
                runKey.DeleteValue(AppInfo.DisplayName, throwOnMissingValue: false);
            }

            WriteShortcut();
        }
        catch (Exception exception)
        {
            // A Locked Registry Must Not Stop Recording
            s_logger.Warn($"Failed To Register With Windows {exception.GetType().Name}");
        }
    }

    private static void WriteShortcut()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(s_shortcutPath)!);

        // WScript Shell Writes Shortcuts Without A Package
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var shortcut = shell.CreateShortcut(s_shortcutPath);
        shortcut.TargetPath = Environment.ProcessPath;
        shortcut.Description = AppInfo.DisplayName;
        shortcut.Save();
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, int flags);
}
