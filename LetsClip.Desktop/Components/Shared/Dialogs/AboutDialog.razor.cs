using LetsClip.Desktop.Interop;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class AboutDialog
{
    private static void OpenNotices()
    {
        // The Single File Unpacks Notices Beside The App
        Explorer.Open(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
    }

    private static void OpenFFmpegLicence()
    {
        Explorer.Open(Path.Combine(AppContext.BaseDirectory, "FFmpeg", "LICENSE.txt"));
    }
}
