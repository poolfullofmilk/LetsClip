using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LetsClip.Data.Helpers;

namespace LetsClip.Desktop.Interop;

public static class AppIcons
{
    // 48 Px Stays Sharp At 24 Px Up To 200%
    private const int Size = 48;

    public static string UrlOf(string? executablePath)
    {
        if (executablePath is null || !File.Exists(executablePath))
        {
            return "";
        }

        var name = $"icon-{FilePaths.KeyOf(executablePath)}.png";
        var file = Path.Combine(AppInfo.ThumbnailsDirectory, name);
        try
        {
            if (!File.Exists(file))
            {
                using var icon = Icon.ExtractIcon(executablePath, 0, Size);
                if (icon is null)
                {
                    return "";
                }

                using var bitmap = icon.ToBitmap();
                bitmap.Save(file, ImageFormat.Png);
            }
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or ExternalException
                        or ArgumentException
            )
        {
            // An Exe Without A Readable Icon Shows The Plain One
            return "";
        }

        return $"https://{DesktopShell.ThumbnailsHost}/{name}";
    }
}
