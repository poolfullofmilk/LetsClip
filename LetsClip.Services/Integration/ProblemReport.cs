using LetsClip.Data.Helpers;

namespace LetsClip.Services.Integration;

public static class ProblemReport
{
    // The Log's End, Within What A GitHub Issue Takes
    private const int LogLines = 200;
    private const int MostCharacters = 60_000;

    public static string IssueUrl()
    {
        // Version And Windows Build Filled In, The Log Pasted Later
        var body =
            $"LetsClip {AppInfo.VersionText} on {AppInfo.WindowsText}\n\nWhat happened:\n\n\nThe log is on your clipboard, paste it here:\n";
        return $"{AppInfo.RepositoryUrl}/issues/new?body={Uri.EscapeDataString(body)}";
    }

    public static string RecentLog()
    {
        try
        {
            // NLog Keeps Today's File Open, So It Is Shared
            var newest = new DirectoryInfo(AppInfo.LogsDirectory)
                .EnumerateFiles("*.txt")
                .MaxBy(file => file.LastWriteTimeUtc);
            if (newest is null)
            {
                return "No log yet";
            }

            using var stream = newest.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');

            // The Profile Folder Reads As Its Name, Nobody's Account Shows
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var recent = string.Join('\n', lines[Math.Max(0, lines.Length - LogLines)..])
                .Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            // Long Command Lines Fill It, The Newest Whole Lines Stay
            return recent.Length <= MostCharacters
                ? recent
                : recent[(recent.IndexOf('\n', recent.Length - MostCharacters) + 1)..];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Unreadable Log Still Lets The Issue Open
            return $"The log could not be read, {exception.GetType().Name}";
        }
    }
}
