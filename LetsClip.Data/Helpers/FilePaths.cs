using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LetsClip.Data.Helpers;

public static class FilePaths
{
    static FilePaths()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static bool AreSame(string? first, string? second)
    {
        // Windows Paths Ignore Case, A Trailing Slash And Dots
        return Normal(first) is { } one
            && Normal(second) is { } other
            && string.Equals(one, other, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInside(string? path, string? folder)
    {
        // The Folder Or Anything Below, Never A Similar Sibling
        if (Normal(path) is not { } full || Normal(folder) is not { } root)
        {
            return false;
        }

        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string KeyOf(string path)
    {
        // One File Name Safe Key Per Path, Any Spelling
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    private static string? Normal(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A Path Windows Cannot Read Matches Nothing
            return null;
        }
    }

    private static bool SelfTestPasses()
    {
        return AreSame(@"C:\Users\Me\Videos\LetsClip", @"c:\users\me\videos\letsclip\")
            && AreSame(@"C:\Users\Me\Videos\..\Videos\LetsClip", @"C:\Users\Me\Videos\LetsClip")
            && !AreSame(@"C:\Users\Me\Videos\LetsClip", @"C:\Users\Me\Videos\LetsClip 2")
            && !AreSame(null, @"C:\")
            && IsInside(
                @"C:\Users\Me\AppData\Local\LetsClip\Cache",
                @"C:\Users\Me\AppData\Local\LetsClip"
            )
            && IsInside(
                @"C:\Users\Me\AppData\Local\LetsClip",
                @"C:\Users\Me\AppData\Local\LetsClip\"
            )
            && !IsInside(
                @"C:\Users\Me\AppData\Local\LetsClip Debug",
                @"C:\Users\Me\AppData\Local\LetsClip"
            )
            && IsInside(@"D:\Clips", @"D:\")
            && !IsInside(@"D:\Clips", null);
    }
}
