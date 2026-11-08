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
        IProgress<(long Received, long Total)> progress
    )
    {
        var started = Stopwatch.StartNew();
        var asset = $"LetsClip_{release.Tag}.exe";
        var targetPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, asset);

        // A Name Only This Attempt Owns, Removed If It Fails
        var partialPath = $"{targetPath}.{Guid.NewGuid():N}.download";
        var expectedDigest = await ExpectedDigestAsync(release.Tag, asset);

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
                    progress.Report((received, total));
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

    private static async Task<string> ExpectedDigestAsync(string tag, string asset)
    {
        // GitHub Hashes Every Release Asset, Its API Lists The Hash
        string? digest = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppInfo.ReleaseApiUrl + tag);
            request.Headers.UserAgent.ParseAdd(AppInfo.DisplayName);
            using var response = await s_checkClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
            ? digest[prefix.Length..].ToLowerInvariant()
            : throw new InvalidDataException($"Release {tag} Lists No Checksum");
    }

    public static void Launch(string executablePath)
    {
        s_logger.Info($"Started Update {executablePath}");
        Process.Start(
            new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                ArgumentList =
                {
                    AfterUpdateArgument,
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    Environment.ProcessPath!,
                },
            }
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
}
