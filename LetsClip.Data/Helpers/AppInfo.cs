using System.Reflection;

namespace LetsClip.Data.Helpers;

public static class AppInfo
{
    public const string DisplayName = "LetsClip";
    public const string Author = "poolfullofmilk";

    // The Update Check Reads Public GitHub Releases
    public const string RepositoryUrl = "https://github.com/poolfullofmilk/LetsClip";
    public const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";
    public const string ReleaseApiUrl =
        "https://api.github.com/repos/poolfullofmilk/LetsClip/releases/tags/";

    public static Version Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    public static string VersionText { get; } = Version.ToString(Version.Build > 0 ? 3 : 2);

    // Debug Builds Never Touch The Installed App's Files
#if DEBUG
    public const string FolderName = "LetsClip Debug";
#else
    public const string FolderName = "LetsClip";
#endif

    // Tests Keep Every Write Under This Folder
    public static readonly string? SandboxRoot = Environment.GetEnvironmentVariable("LETSCLIP_ROOT")
        is { Length: > 0 } root
        ? Path.GetFullPath(root)
        : null;

    // Settings, Clip Metadata And The Browser Profile
    public static readonly string DataDirectory = Path.Combine(
        BaseOf(Environment.SpecialFolder.ApplicationData, "AppData"),
        FolderName
    );

    // Disposable Replay Cache And Thumbnails
    public static readonly string LocalDirectory = Path.Combine(
        BaseOf(Environment.SpecialFolder.LocalApplicationData, "LocalAppData"),
        FolderName
    );

    public static readonly string CacheDirectory = Path.Combine(LocalDirectory, "Cache");

    public static readonly string ThumbnailsDirectory = Path.Combine(LocalDirectory, "Thumbnails");

    public static readonly string LogsDirectory = Path.Combine(
        BaseOf(Environment.SpecialFolder.MyDocuments, "Documents"),
        FolderName,
        "Logs"
    );

    // Saved Clips Default To The Videos Known Folder
    public static readonly string DefaultClipsDirectory = Path.Combine(
        BaseOf(Environment.SpecialFolder.MyVideos, "Videos"),
        FolderName
    );

    private static string BaseOf(Environment.SpecialFolder folder, string sandboxName)
    {
        return SandboxRoot is { } sandbox
            ? Path.Combine(sandbox, sandboxName)
            : Environment.GetFolderPath(folder);
    }
}
