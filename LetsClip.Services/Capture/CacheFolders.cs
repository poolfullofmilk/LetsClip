using System.Diagnostics;
using System.Text.RegularExpressions;
using LetsClip.Data.Helpers;
using NLog;

namespace LetsClip.Services.Capture;

public static partial class CacheFolders
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    static CacheFolders()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static string NewFolder()
    {
        // A Fresh Identity Per Session, Leftovers Are Never Trusted
        var folder = Path.Combine(AppInfo.CacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void DeleteLeftovers(string? keep)
    {
        if (!Directory.Exists(AppInfo.CacheDirectory))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(AppInfo.CacheDirectory))
        {
            if (!string.Equals(folder, keep, StringComparison.OrdinalIgnoreCase))
            {
                Delete(folder);
            }
        }
    }

    public static void Delete(string folder)
    {
        try
        {
            // Only A Guid Folder Under The Cache, Never Through Links
            if (!IsOwnedFolder(folder, AppInfo.CacheDirectory))
            {
                s_logger.Warn($"Refused To Delete {folder}");
                return;
            }

            var directory = new DirectoryInfo(folder);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            foreach (var file in directory.EnumerateFiles())
            {
                if (IsOwnedFile(file.Name) && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    file.Delete();
                }
            }

            if (!directory.EnumerateFileSystemInfos().Any())
            {
                directory.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A Locked Leftover Waits For The Next Start
            s_logger.Warn($"Failed To Clean Cache {exception.GetType().Name}");
        }
    }

    public static bool IsOwnedFolder(string folder, string cacheRoot)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(Path.GetFileName(full), "N", out _);
    }

    public static bool IsOwnedFile(string name)
    {
        return OwnedFileName().IsMatch(name);
    }

    [GeneratedRegex(@"^((seg|aud)_\d{6}\.ts|export-pass(-\d+)?\.log(\.mbtree)?(\.temp)?)$")]
    private static partial Regex OwnedFileName();

    private static bool SelfTestPasses()
    {
        var root = Path.Combine(Path.GetTempPath(), "Root", "Cache");
        var session = Path.Combine(root, Guid.NewGuid().ToString("N"));
        return IsOwnedFolder(session, root)
            && IsOwnedFolder(session + Path.DirectorySeparatorChar, root)
            && !IsOwnedFolder(root, root)
            && !IsOwnedFolder(Path.Combine(root, "Clips"), root)
            && !IsOwnedFolder(Path.Combine(session, "..", "..", "Videos"), root)
            && !IsOwnedFolder(Path.Combine(root, Guid.NewGuid().ToString("N"), "Inner"), root)
            && IsOwnedFile("seg_000123.ts")
            && IsOwnedFile("aud_000001.ts")
            && IsOwnedFile("export-pass-0.log.mbtree")
            && !IsOwnedFile("clip.mp4")
            && !IsOwnedFile("seg_1.ts.mp4");
    }
}
