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
    // Releases Always Carry One Exe Named After Their Tag
    private const string ExecutablePrefix = "LetsClip_v";
    private const string AfterUpdateArgument = "--after-update";

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

            return Version.TryParse(tag.TrimStart('v', 'V'), out var latest)
                ? new ReleaseInfo(tag, latest)
                : null;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException)
        {
            // Unreachable Means Unknown, Never Up To Date
            return null;
        }
    }

    // A Sandboxed Or Renamed Run Never Replaces An Exe
    public static bool CanInstall =>
        AppInfo.SandboxRoot == null
        && Path.GetFileName(Environment.ProcessPath)
            ?.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase) == true;

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
            && !IsSigned(new MemoryStream("LetsClip"u8.ToArray()), "not a signature");
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

    // A Restart Waits For The Crashed Copy, Like An Update
    public static string RestartArguments =>
        $"{AfterUpdateArgument} {Environment.ProcessId} \"{Environment.ProcessPath}\" --tray";

    public static bool MoveToInstallFolder(string[] arguments)
    {
        // Run From Downloads Once, It Lives In One Place After
        var running = Environment.ProcessPath!;
        if (
            !CanInstall
            || FilePaths.AreSame(Path.GetDirectoryName(running), AppInfo.InstallDirectory)
        )
        {
            return false;
        }

        try
        {
            var installed = Path.Combine(AppInfo.InstallDirectory, Path.GetFileName(running));
            Directory.CreateDirectory(AppInfo.InstallDirectory);
            if (
                !File.Exists(installed)
                || new FileInfo(installed).Length != new FileInfo(running).Length
            )
            {
                File.Copy(running, installed, overwrite: true);
            }

            var startInfo = new ProcessStartInfo(installed) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Unwritable Or Blocked, It Runs From Where It Is
            return false;
        }
    }

    public static void Launch(string executablePath, bool inTray)
    {
        // An Update While Hidden Stays In The Tray
        s_logger.Info($"Started Update {executablePath}{(inTray ? " In The Tray" : "")}");
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            ArgumentList =
            {
                AfterUpdateArgument,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                Environment.ProcessPath!,
            },
        };
        if (inTray)
        {
            startInfo.ArgumentList.Add("--tray");
        }

        Process.Start(startInfo);
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

    public static void DeletePreviousVersion(string? previousPath)
    {
        // Only After This Version Started, Only The Exe It Replaced
        if (
            previousPath is null
            || FilePaths.AreSame(previousPath, Environment.ProcessPath)
            || !FilePaths.AreSame(
                Path.GetDirectoryName(previousPath),
                Path.GetDirectoryName(Environment.ProcessPath)
            )
            || !Path.GetFileName(previousPath)
                .StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        try
        {
            File.Delete(previousPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A Locked Old Exe Harms Nothing
            s_logger.Warn($"Failed To Delete The Previous Version {exception.GetType().Name}");
        }
    }

    public static void DeleteOldUnpackedVersions()
    {
        // Each Version Unpacks 430 MB Into %Temp%\.net, Never Removed
        var current = new DirectoryInfo(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        );
        if (
            AppInfo.SandboxRoot is not null
            || current.Parent is not { } version
            || !version.Name.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase)
            || version.Parent is not { Name: ".net" } root
        )
        {
            return;
        }

        var unpackedFolders = root.EnumerateDirectories(ExecutablePrefix + "*")
            .SelectMany(folder => folder.EnumerateDirectories())
            .Where(unpacked => !FilePaths.AreSame(unpacked.FullName, current.FullName))
            .ToList();
        foreach (var unpacked in unpackedFolders)
        {
            DeleteUnpacked(unpacked);
        }
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
}
