using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Integration;

public static class Updater
{
    // Releases Carry One Tagged Exe, Installed As LetsClip.exe
    private const string ExecutablePrefix = "LetsClip_v";
    private const string AfterUpdateArgument = "--after-update";
    private const string InstalledFromArgument = "--installed-from";
    public const string UninstallArgument = "--uninstall";

    // The Running LetsClip Waits On These
    public const string ShowSignal = "Show";
    public const string ExitSignal = "Exit";

    // The Downloaded Copy Needs A Moment To Exit
    private const int DownloadDeleteAttempts = 10;

    // The Owner's Public Key, The Private Half Stays Off GitHub
    private const string SigningKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXVuVWirBvHy4LvU4Tw+CV1+JO9hQWAiLIGOvP2QIWFAgmXclQKJ7laFwF8gttv+X7cZDkXiowsEkOxeWck30VQ==";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // Checks Read The Redirect Itself, Downloads Follow It
    private static readonly HttpClient s_checkClient = new(
        new HttpClientHandler { AllowAutoRedirect = false }
    )
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private static readonly HttpClient s_downloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    // The Old Version May Finish A Save First
    private static readonly TimeSpan s_previousExitWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_stallTimeout = TimeSpan.FromMinutes(1);

    static Updater()
    {
        Debug.Assert(SelfTestPasses(), "Updater SelfTest Failed");
    }

    public static async Task<ReleaseInfo?> CheckAsync()
    {
        try
        {
            // The Latest Release Url Redirects To The Tagged Release
            using var response = await s_checkClient.GetAsync(
                AppInfo.LatestReleaseUrl,
                HttpCompletionOption.ResponseHeadersRead
            );
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            var tag = location[(location.LastIndexOf('/') + 1)..];
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                s_logger.Debug($"Checked For Updates, The Latest Release Is {tag}");
                return new ReleaseInfo(tag, latest);
            }

            // No Redirect Means GitHub Lists No Release Right Now
            s_logger.Info(
                $"Failed To Check For Updates, GitHub Answered {(int)response.StatusCode} Without A Release"
            );
            return null;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException)
        {
            // Unreachable Means Unknown, Never Up To Date
            s_logger.Info($"Failed To Check For Updates {exception.GetType().Name}");
            return null;
        }
    }

    public static async Task<IReadOnlyList<string>?> ChangesAsync(string? from, string? to)
    {
        // Commit Titles Between Two Builds, Newest First, Null When Unreadable
        if (from is null || to is null || from == to)
        {
            return [];
        }

        try
        {
            // Ponytail: GitHub Compares 250 Commits At Most, Page Beyond That
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{AppInfo.CompareApiUrl}{from}...{to}"
            );
            request.Headers.UserAgent.ParseAdd(AppInfo.DisplayName);
            using var response = await s_checkClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var comparison = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return
            [
                .. comparison
                    .RootElement.GetProperty("commits")
                    .EnumerateArray()
                    .Reverse()
                    .Select(entry =>
                        entry
                            .GetProperty("commit")
                            .GetProperty("message")
                            .GetString()
                            ?.Split('\n')[0]
                            .Trim()
                    )
                    .OfType<string>()
                    .Where(title => title.Length > 0)
                    .Distinct(),
            ];
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or TaskCanceledException
                        or JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
            )
        {
            // The Changes Are Extra, Asked Again Next Time
            s_logger.Info($"Failed To Read What Changed {exception.GetType().Name}");
            return null;
        }
    }

    // A Sandboxed, Debug Or Renamed Run Never Replaces An Exe
    public static bool CanInstall =>
        AppInfo.SandboxRoot == null
        && (
            IsVersioned(Environment.ProcessPath)
            || string.Equals(
                Path.GetFileName(Environment.ProcessPath),
                Path.GetFileName(AppInfo.InstallPath),
                StringComparison.OrdinalIgnoreCase
            )
        );

    private static bool IsVersioned(string? path)
    {
        // A Download Or A Version Set Aside, Never LetsClip.exe Itself
        var name = Path.GetFileName(path) ?? "";
        return name.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> DownloadAsync(
        ReleaseInfo release,
        IProgress<(long Received, long Total)>? progress
    )
    {
        var started = Stopwatch.StartNew();
        var asset = $"LetsClip_{release.Tag}.exe";
        Directory.CreateDirectory(AppInfo.InstallDirectory);
        var targetPath = Path.Combine(AppInfo.InstallDirectory, asset);

        // A Name Only This Attempt Owns, Removed If It Fails
        var partialPath = $"{targetPath}.{Guid.NewGuid():N}.download";
        var (expectedDigest, signature) = await ExpectedDetailsAsync(release.Tag, asset);

        // A Body Stalled For A Minute Is Given Up
        using var stall = new CancellationTokenSource(s_stallTimeout);
        try
        {
            using var response = await s_downloadClient.GetAsync(
                $"{AppInfo.RepositoryUrl}/releases/download/{release.Tag}/{asset}",
                HttpCompletionOption.ResponseHeadersRead,
                stall.Token
            );
            response.EnsureSuccessStatusCode();
            await using (
                var file = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous
                )
            )
            {
                // Zero Total Means GitHub Sent No Length
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var download = await response.Content.ReadAsStreamAsync(stall.Token);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await download.ReadAsync(buffer, stall.Token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    stall.CancelAfter(s_stallTimeout);
                    received += read;
                    progress?.Report((received, total));
                }
            }

            // A Download That Differs From GitHub's Checksum Never Runs
            await using (var downloaded = File.OpenRead(partialPath))
            {
                var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(downloaded));
                if (digest != expectedDigest)
                {
                    throw new InvalidDataException($"Checksum Mismatch For {release.Tag}");
                }

                // GitHub Vouches For The Download, The Signature For Its Author
                downloaded.Position = 0;
                if (!IsSigned(downloaded, signature))
                {
                    throw new InvalidDataException($"Bad Signature For {release.Tag}");
                }
            }

            // Only A Complete Download Gets The Real Name
            File.Move(partialPath, targetPath, overwrite: true);
            s_logger.Info(
                $"Downloaded Update {release.Tag} In {started.Elapsed.TotalSeconds:0.0} s To {targetPath}"
            );
            return targetPath;
        }
        catch
        {
            File.Delete(partialPath);
            throw;
        }
    }

    private static bool IsSigned(Stream file, string? signature)
    {
        // An ECDSA P-256 Signature Of The Exe, OpenSSL's Form
        var bytes = new byte[128];
        if (!Convert.TryFromBase64String(signature ?? "", bytes, out var length))
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(SigningKey), out _);
        return key.VerifyData(
            file,
            bytes.AsSpan(0, length).ToArray(),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence
        );
    }

    private static bool SelfTestPasses()
    {
        // A Known Signature Holds, A Changed Byte Fails
        const string signed =
            "MEYCIQDrMuNndm0M94jttveQr5mazeLA5AKunFRNwQne4Nac6gIhAOjHUf4usmtVqF+Nc9DUNqZta8Y8FuPZGmrs3mJoZmPA";
        return IsSigned(new MemoryStream("LetsClip"u8.ToArray()), signed)
            && !IsSigned(new MemoryStream("LetsClap"u8.ToArray()), signed)
            && !IsSigned(new MemoryStream("LetsClip"u8.ToArray()), null)
            && !IsSigned(new MemoryStream("LetsClip"u8.ToArray()), "not a signature")
            && IsVersioned(@"C:\Downloads\LetsClip_v2.1.exe")
            && !IsVersioned(@"C:\Programs\LetsClip\LetsClip.exe")
            && !IsVersioned(@"C:\Programs\LetsClip\LetsClip_v2.1.exe.download");
    }

    private static async Task<(string Digest, string? Signature)> ExpectedDetailsAsync(
        string tag,
        string asset
    )
    {
        // GitHub Hashes Every Release Asset, The Body Holds The Signature
        string? digest = null;
        string? signature = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppInfo.ReleaseApiUrl + tag);
            request.Headers.UserAgent.ParseAdd(AppInfo.DisplayName);
            using var response = await s_checkClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            signature = release.RootElement.TryGetProperty("body", out var body)
                ? body.GetString()?.Trim()
                : null;
            if (release.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var listed in assets.EnumerateArray())
                {
                    if (
                        listed.TryGetProperty("name", out var name)
                        && name.GetString() == asset
                        && listed.TryGetProperty("digest", out var value)
                    )
                    {
                        digest = value.GetString();
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Unreadable Release Details For {tag}", exception);
        }

        const string prefix = "sha256:";
        return digest?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? (digest[prefix.Length..].ToLowerInvariant(), signature)
            : throw new InvalidDataException($"Release {tag} Lists No Checksum");
    }

    #region Install
    // A Restart Waits For The Crashed Copy, Like An Update
    public static string RestartArguments =>
        $"{AfterUpdateArgument} {Environment.ProcessId} \"{Environment.ProcessPath}\" --tray";

    public static bool MoveToInstallFolder(string[] arguments)
    {
        // Every Release Runs As Programs\LetsClip\LetsClip.exe
        var running = Environment.ProcessPath!;
        if (!CanInstall || FilePaths.AreSame(running, AppInfo.InstallPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(AppInfo.InstallDirectory);

            // A Newer Download Takes Over From A Running Install
            if (RunningInstall() is { } occupant)
            {
                using (occupant)
                {
                    return TakeOver(occupant, running, arguments);
                }
            }

            File.Copy(running, AppInfo.InstallPath, overwrite: true);
            Start(AppInfo.InstallPath, [.. arguments, InstalledFromArgument, running]);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Unwritable Or Blocked, It Runs From Where It Is
            return false;
        }
    }

    private static bool TakeOver(Process occupant, string running, string[] arguments)
    {
        // The Same Or A Newer Install Just Shows Its Window
        var installed = VersionOf(AppInfo.InstallPath);
        if (installed >= AppInfo.Version)
        {
            return false;
        }

        // Set Aside Until The New One Starts, Asked To Exit
        var aside = AsidePath(installed);
        File.Move(AppInfo.InstallPath, aside, overwrite: true);
        try
        {
            File.Copy(running, AppInfo.InstallPath);
        }
        catch
        {
            File.Move(aside, AppInfo.InstallPath);
            throw;
        }

        Signal(ExitSignal);
        Start(
            AppInfo.InstallPath,
            [
                .. arguments,
                AfterUpdateArgument,
                occupant.Id.ToString(CultureInfo.InvariantCulture),
                aside,
                InstalledFromArgument,
                running,
            ]
        );
        return true;
    }

    private static Process? RunningInstall()
    {
        // Another LetsClip Running From The Install Path
        var name = Path.GetFileNameWithoutExtension(AppInfo.InstallPath);
        Process? found = null;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (
                    found is null
                    && process.Id != Environment.ProcessId
                    && FilePaths.AreSame(process.MainModule?.FileName, AppInfo.InstallPath)
                )
                {
                    found = process;
                    continue;
                }
            }
            catch (Exception exception)
                when (exception is Win32Exception or InvalidOperationException)
            {
                // Gone Or Elevated, Not Ours To Replace
            }

            process.Dispose();
        }

        return found;
    }

    private static Version VersionOf(string path)
    {
        return Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var version)
            ? version
            : new Version(0, 0);
    }

    private static string AsidePath(Version version)
    {
        // Named Like Its Release, So Every Cleanup Knows It
        return Path.Combine(
            AppInfo.InstallDirectory,
            $"{ExecutablePrefix}{version.ToString(version.Build > 0 ? 3 : 2)}.exe"
        );
    }

    private static void Signal(string signal)
    {
        // The Running LetsClip Listens, Nothing Happens If None Runs
        if (EventWaitHandle.TryOpenExisting(AppInfo.SignalName(signal), out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }

    private static void Start(string executablePath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo)?.Dispose();
    }

    public static string? DownloadOf(string[] arguments)
    {
        // Set Only By The Copy That Just Installed This One
        var index = Array.IndexOf(arguments, InstalledFromArgument);
        return AppInfo.SandboxRoot is null && index >= 0 && index + 1 < arguments.Length
            ? arguments[index + 1]
            : null;
    }

    public static void DeleteDownload(string download)
    {
        // Only A Versioned LetsClip Exe, Never The One Running
        if (!IsVersioned(download) || FilePaths.AreSame(download, Environment.ProcessPath))
        {
            return;
        }

        for (var attempt = 1; File.Exists(download); attempt++)
        {
            try
            {
                File.Delete(download);
                s_logger.Info($"Deleted The Download {download}");
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // Still Running Or Locked, In The End It Stays
                if (attempt == DownloadDeleteAttempts)
                {
                    s_logger.Info($"Kept The Download {exception.GetType().Name}");
                    return;
                }

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    public static void Launch(string executablePath, bool inTray)
    {
        // The Installed Exe Steps Aside Until The New One Starts
        var previous = Environment.ProcessPath!;
        var target = executablePath;
        if (
            FilePaths.AreSame(previous, AppInfo.InstallPath)
            && !FilePaths.AreSame(executablePath, AppInfo.InstallPath)
        )
        {
            var aside = AsidePath(AppInfo.Version);
            try
            {
                File.Move(previous, aside, overwrite: true);
                try
                {
                    File.Move(executablePath, AppInfo.InstallPath);
                }
                catch
                {
                    File.Move(aside, previous);
                    throw;
                }

                (previous, target) = (aside, AppInfo.InstallPath);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // The New Exe Takes The Name Once This One Exits
                s_logger.Warn($"Failed To Swap The Installed Exe {exception.GetType().Name}");
            }
        }

        // An Update While Hidden Stays In The Tray
        s_logger.Info($"Started Update {target}{(inTray ? " In The Tray" : "")}");
        Start(
            target,
            [
                AfterUpdateArgument,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                previous,
                .. inTray ? ["--tray"] : Array.Empty<string>(),
            ]
        );
    }

    public static string? WaitForPreviousVersion(string[] arguments)
    {
        var index = Array.IndexOf(arguments, AfterUpdateArgument);
        if (
            AppInfo.SandboxRoot != null
            || index < 0
            || index + 1 >= arguments.Length
            || !int.TryParse(arguments[index + 1], out var processId)
        )
        {
            return null;
        }

        // Newer Versions Name Themselves, Older Ones Are Asked
        var previousPath = index + 2 < arguments.Length ? arguments[index + 2] : null;
        try
        {
            using var previous = Process.GetProcessById(processId);
            previousPath ??= previous.MainModule?.FileName;

            // Still Running After The Wait, Its Exe Stays
            if (!previous.WaitForExit(s_previousExitWait))
            {
                s_logger.Warn($"Found Previous Version {processId} Still Running");
                return null;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception)
        {
            // Already Gone, Or It Would Not Say Where It Lives
        }

        return previousPath;
    }

    public static void DeleteOtherVersions()
    {
        // Started Well As The Installed Exe, Older Ones Go
        if (
            AppInfo.SandboxRoot is not null
            || !FilePaths.AreSame(Environment.ProcessPath, AppInfo.InstallPath)
        )
        {
            return;
        }

        foreach (
            var older in Directory.EnumerateFiles(
                AppInfo.InstallDirectory,
                ExecutablePrefix + "*.exe"
            )
        )
        {
            try
            {
                File.Delete(older);
                s_logger.Info($"Deleted The Older Version {Path.GetFileName(older)}");
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A Running Or Locked Old Exe Goes Next Time
                s_logger.Warn($"Failed To Delete An Older Version {exception.GetType().Name}");
            }
        }
    }

    public static void DeleteOldUnpackedVersions()
    {
        // Each Version Unpacks Into %Temp%\.net, Never Removed By Windows
        var current = new DirectoryInfo(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        );
        if (
            AppInfo.SandboxRoot is not null
            || current.Parent is not { } version
            || !IsUnpackedName(version.Name)
            || version.Parent is not { Name: ".net" } root
        )
        {
            return;
        }

        var unpackedFolders = root.EnumerateDirectories()
            .Where(folder => IsUnpackedName(folder.Name))
            .SelectMany(folder => folder.EnumerateDirectories())
            .Where(unpacked => !FilePaths.AreSame(unpacked.FullName, current.FullName))
            .ToList();
        foreach (var unpacked in unpackedFolders)
        {
            DeleteUnpacked(unpacked);
        }
    }

    private static bool IsUnpackedName(string name)
    {
        // Named After The Exe, LetsClip Or An Older LetsClip_v Name
        return name.Equals(AppInfo.DisplayName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteUnpacked(DirectoryInfo unpacked)
    {
        var version = unpacked.Parent!;
        try
        {
            // Open Files Block A Rename, So A Running Copy Stays
            if (!unpacked.Name.EndsWith("-old", StringComparison.Ordinal))
            {
                unpacked.MoveTo(unpacked.FullName + "-old");
            }

            unpacked.Delete(recursive: true);
            if (!version.EnumerateFileSystemInfos().Any())
            {
                version.Delete();
            }

            s_logger.Info($"Deleted The Unpacked {version.Name}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // In Use Or Locked, The Next Start Tries Again
            s_logger.Debug($"Kept The Unpacked {version.Name}, {exception.GetType().Name}");
        }
    }
    #endregion

    #region Uninstall
    public static void Uninstall()
    {
        // The Running LetsClip Exits First, So Its Files Let Go
        Signal(ExitSignal);
        WaitForOtherCopies();
        StartupRegistration.Remove();

        // Rebuilt By Any Start, Never Clips, Details Or Settings
        DeleteFolder(AppInfo.LocalDirectory);
        DeleteFolder(AppInfo.LogsDirectory);
        DeleteFolder(Path.GetDirectoryName(AppInfo.LogsDirectory)!, onlyEmpty: true);
        DeleteOldUnpackedVersions();
    }

    public static void DeleteInstallAfterExit()
    {
        // A Running Exe Cannot Delete Itself, Cmd Does It After
        if (AppInfo.SandboxRoot is not null)
        {
            return;
        }

        // The Install Folder, Browser Profile And Unpacked Files
        List<string> folders =
        [
            AppInfo.InstallDirectory,
            Path.Combine(AppInfo.DataDirectory, "WebView2"),
        ];
        var unpacked = new DirectoryInfo(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        ).Parent;
        if (unpacked is { Parent.Name: ".net" } && IsUnpackedName(unpacked.Name))
        {
            folders.Add(unpacked.FullName);
        }

        var removals = string.Join(" & ", folders.Select(folder => $"rmdir /s /q \"{folder}\""));
        // Cmd Reads Its Line Raw, Quoted Paths Only
        Process
            .Start(
                new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 > nul & {removals}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),
                }
            )
            ?.Dispose();
    }

    private static void WaitForOtherCopies()
    {
        // Every Other LetsClip, Any Version, Gets Its Minute
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && IsUnpackedName(process.ProcessName))
                {
                    try
                    {
                        process.WaitForExit(s_previousExitWait);
                    }
                    catch (Exception exception)
                        when (exception is Win32Exception or InvalidOperationException)
                    {
                        // Gone Meanwhile
                    }
                }
            }
        }
    }

    private static void DeleteFolder(string folder, bool onlyEmpty = false)
    {
        try
        {
            if (
                Directory.Exists(folder)
                && (!onlyEmpty || !Directory.EnumerateFileSystemEntries(folder).Any())
            )
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A Locked Leftover Harms Nothing, The Clips Are Untouched
        }
    }
    #endregion
}
