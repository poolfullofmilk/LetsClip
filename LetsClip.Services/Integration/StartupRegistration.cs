using LetsClip.Data.Helpers;
using Microsoft.Win32;

namespace LetsClip.Services.Integration;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Desktop Apps Folder Links To This Start Menu Folder
    private static readonly string s_shortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "01 Apps",
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
        catch
        {
            // A Locked Registry Must Not Stop Recording
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
}
