using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LetsClip.Services.Games;

internal static partial class GameScanner
{
    // Executable Name Tokens That Never Compete
    private static readonly string[] s_executableRejectTokens =
    [
        "helper",
        "handler",
        "video",
        "service",
        "crash",
        "report",
        "uninstall",
        "setup",
    ];

    // Executable Name Tokens Earning Three Points Each
    private static readonly string[] s_executableBonusTokens = ["win64", "shipping"];

    // Games Whose Folder Name Reads Badly
    private static readonly Dictionary<string, string> s_displayNameAliases = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["assettocorsa"] = "Assetto Corsa",
        ["valorant"] = "Valorant",
        ["rocketleague"] = "Rocket League",
        ["robloxplayerbeta"] = "Roblox",
        ["repo"] = "R.E.P.O.",
    };

    // Subdirectories Games Commonly Hide Their Executable In
    private static readonly string[] s_executableSubdirectories =
    [
        "",
        "Binaries",
        @"Binaries\Win64",
        @"bin\win64",
        @"game\bin\win64",
        @"live\ShooterGame\Binaries\Win64",
    ];

    private static readonly string[] s_steamRoots =
    [
        @"C:\Program Files (x86)\Steam",
        @"C:\Program Files\Steam",
    ];

    private static readonly string[] s_rockstarRoots =
    [
        @"C:\Program Files\Rockstar Games",
        @"C:\Program Files (x86)\Rockstar Games",
    ];

    private static readonly string[] s_commonGameRoots = [@"C:\Games", @"D:\Games", @"E:\Games"];

    // A Failed Scan Is Retried Instead Of Cached
    private static readonly Lazy<Dictionary<string, string>> s_cache = new(
        Scan,
        LazyThreadSafetyMode.PublicationOnly
    );

    static GameScanner()
    {
        Debug.Assert(SelfTestPasses(), "GameScanner SelfTest Failed");
    }

    public static IReadOnlyDictionary<string, string> InstalledGames => s_cache.Value;

    #region Scan
    private static Dictionary<string, string> Scan()
    {
        Dictionary<string, string> installedGames = new(StringComparer.OrdinalIgnoreCase);

        var gameRoots = DiscoverSteamGameRoots()
            .Concat(DiscoverEpicGameRoots())
            .Concat(DiscoverRiotGameRoots())
            .Concat(DiscoverRockstarGameRoots())
            .Concat(DiscoverRobloxGameRoots())
            .Concat(s_commonGameRoots.SelectMany(EnumerateDirectoriesSafely));

        foreach (var rootDirectory in gameRoots)
        {
            var folderName = new DirectoryInfo(rootDirectory).Name;
            var executableName = ResolveMainExecutableName(rootDirectory, folderName);
            if (executableName is null)
            {
                continue;
            }

            var processName = NormalizeProcessKey(executableName);

            // The First Folder Claiming A Key Keeps It
            if (processName.Length >= 2 && processName != "launcher")
            {
                installedGames.TryAdd(processName, BuildDisplayName(folderName, processName));
            }
        }

        return installedGames;
    }

    private static string BuildDisplayName(string folderName, string processName)
    {
        if (s_displayNameAliases.TryGetValue(processName, out var alias))
        {
            return alias;
        }

        // A Folder With Spaces Or Capitals Reads Fine
        return folderName.Any(char.IsUpper) || folderName.Contains(' ')
            ? folderName
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(folderName);
    }

    private static string? ResolveMainExecutableName(string rootDirectory, string folderName)
    {
        string? bestExecutableName = null;
        var bestScore = int.MinValue;

        foreach (var subdirectory in s_executableSubdirectories)
        {
            var probeDirectory = Path.Combine(rootDirectory, subdirectory);

            foreach (var executablePath in EnumerateFilesSafely(probeDirectory, "*.exe"))
            {
                var executableName = Path.GetFileNameWithoutExtension(executablePath);

                // Vetoed Names Never Compete
                if (IsRejectedExecutable(executableName))
                {
                    continue;
                }

                var score = ScoreExecutable(executableName, folderName);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestExecutableName = executableName;
                }
            }
        }

        return bestExecutableName;
    }

    private static bool IsRejectedExecutable(string executableName) =>
        s_executableRejectTokens.Any(token =>
            executableName.Contains(token, StringComparison.OrdinalIgnoreCase)
        );

    private static int ScoreExecutable(string executableName, string folderName)
    {
        var score =
            (
                s_executableBonusTokens.Count(token =>
                    executableName.Contains(token, StringComparison.OrdinalIgnoreCase)
                ) * 3
            ) - (executableName.Contains("launcher", StringComparison.OrdinalIgnoreCase) ? 5 : 0);

        // Reward A Name Matching Its Folder
        if (executableName.Equals(folderName, StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }
        else if (folderName.Contains(executableName, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return score;
    }

    public static string NormalizeProcessKey(string name)
    {
        var cleaned = (
            name.Contains('_') || name.Contains('-') ? name.Replace("_", "").Replace("-", "") : name
        ).ToLowerInvariant();

        // Games Shipping Under Many Executable Names
        if (cleaned is "acs" or "acsx86" || cleaned.Contains("assettocorsa"))
        {
            return "assettocorsa";
        }

        return cleaned.Contains("valorant") ? "valorant" : cleaned;
    }
    #endregion

    #region Steam
    private static List<string> DiscoverSteamGameRoots()
    {
        List<string> roots = [];

        try
        {
            var libraryFolders = File.ReadAllText(
                Path.Combine(
                    s_steamRoots.First(Directory.Exists),
                    "steamapps",
                    "libraryfolders.vdf"
                )
            );

            foreach (Match libraryMatch in LibraryPathRegex().Matches(libraryFolders))
            {
                var steamAppsDirectory = Path.Combine(
                    libraryMatch.Groups["libraryPath"].Value.Replace(@"\\", @"\"),
                    "steamapps"
                );

                foreach (
                    var manifestPath in EnumerateFilesSafely(
                        steamAppsDirectory,
                        "appmanifest_*.acf"
                    )
                )
                {
                    var installMatch = InstallDirectoryRegex()
                        .Match(File.ReadAllText(manifestPath));
                    if (!installMatch.Success)
                    {
                        continue;
                    }

                    var gameRoot = Path.Combine(
                        steamAppsDirectory,
                        "common",
                        installMatch.Groups["installDirectory"].Value
                    );

                    if (Directory.Exists(gameRoot))
                    {
                        roots.Add(gameRoot);
                    }
                }
            }
        }
        catch
        {
            // An Unreadable Library Only Means Fewer Known Games
        }

        return roots;
    }
    #endregion

    #region Epic
    private static List<string> DiscoverEpicGameRoots()
    {
        List<string> roots = [];

        try
        {
            foreach (
                var itemFilePath in EnumerateFilesSafely(
                    @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests",
                    "*.item"
                )
            )
            {
                using var jsonDocument = JsonDocument.Parse(File.ReadAllText(itemFilePath));
                var installLocation = jsonDocument
                    .RootElement.GetProperty("InstallLocation")
                    .GetString();

                if (Directory.Exists(installLocation))
                {
                    roots.Add(installLocation);
                }
            }
        }
        catch
        {
            // An Unreadable Library Only Means Fewer Known Games
        }

        return roots;
    }
    #endregion

    #region Riot
    private static List<string> DiscoverRiotGameRoots()
    {
        List<string> roots = [];

        try
        {
            using var jsonDocument = JsonDocument.Parse(
                File.ReadAllText(@"C:\ProgramData\Riot Games\RiotClientInstalls.json")
            );

            foreach (var property in jsonDocument.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                // Two Levels Up Sits The Riot Root
                var riotRoot = Path.GetDirectoryName(
                    Path.GetDirectoryName(property.Value.GetString()?.TrimEnd('\\', '/'))
                );

                roots.AddRange(EnumerateDirectoriesSafely(riotRoot));
            }
        }
        catch
        {
            // An Unreadable Library Only Means Fewer Known Games
        }

        return roots;
    }
    #endregion

    #region Rockstar
    private static List<string> DiscoverRockstarGameRoots()
    {
        List<string> roots = [];

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Rockstar Games");

            foreach (var subKeyName in baseKey?.GetSubKeyNames() ?? [])
            {
                using var subKey = baseKey!.OpenSubKey(subKeyName);
                var installLocation = (
                    (subKey?.GetValue("InstallFolder") as string)
                    ?? (subKey?.GetValue("InstallLocation") as string)
                )?.TrimEnd('\\', '/');

                if (Directory.Exists(installLocation))
                {
                    roots.Add(installLocation);
                }
            }
        }
        catch
        {
            // An Unreadable Library Only Means Fewer Known Games
        }

        roots.AddRange(s_rockstarRoots.SelectMany(EnumerateDirectoriesSafely));
        return roots;
    }
    #endregion

    #region Roblox
    private static IEnumerable<string> DiscoverRobloxGameRoots() =>
        EnumerateDirectoriesSafely(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox",
                    "Versions"
                )
            )
            .Where(directory => File.Exists(Path.Combine(directory, "RobloxPlayerBeta.exe")))
            .Take(1);
    #endregion

    #region File System
    private static IEnumerable<string> EnumerateFilesSafely(string directoryPath, string pattern) =>
        Directory.Exists(directoryPath)
            ? Directory.EnumerateFiles(directoryPath, pattern, new EnumerationOptions())
            : [];

    private static IEnumerable<string> EnumerateDirectoriesSafely(string? directoryPath) =>
        Directory.Exists(directoryPath)
            ? Directory.EnumerateDirectories(directoryPath, "*", new EnumerationOptions())
            : [];
    #endregion

    #region Self Test
    private static bool SelfTestPasses()
    {
        if (
            NormalizeProcessKey("acs") != "assettocorsa"
            || NormalizeProcessKey("ACShadows") != "acshadows"
            || NormalizeProcessKey("VALORANT-Win64-Shipping") != "valorant"
        )
        {
            return false;
        }

        // The Game Must Outrank Its Launcher
        if (
            ScoreExecutable("FortniteClient-Win64-Shipping", "Fortnite")
            <= ScoreExecutable("FortniteLauncher", "Fortnite")
        )
        {
            return false;
        }

        // A Written Folder Name Beats The Executable Name
        if (
            BuildDisplayName("assettocorsa", "assettocorsa") != "Assetto Corsa"
            || BuildDisplayName("Sons Of The Forest", "sonsoftheforest") != "Sons Of The Forest"
            || BuildDisplayName("cuphead", "cuphead") != "Cuphead"
        )
        {
            return false;
        }

        // Vetoed Names Must Be Dropped Before Scoring
        string[] rejected =
        [
            "RustCrashHandler",
            "CrsVideo",
            "Uninstall",
            "EasyAntiCheat_Setup",
            "SomeService",
        ];

        return rejected.All(IsRejectedExecutable) && !IsRejectedExecutable("Rust");
    }
    #endregion

    #region Regexes
    [GeneratedRegex("\"installdir\"\\s*\"(?<installDirectory>[^\"]+)\"")]
    private static partial Regex InstallDirectoryRegex();

    [GeneratedRegex("\"path\"\\s*\"(?<libraryPath>[^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
    #endregion
}
