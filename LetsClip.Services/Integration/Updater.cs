using System.Diagnostics;
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
    private const string AssetUrlFormat =
        AppInfo.RepositoryUrl + "/releases/download/{0}/LetsClip_{0}.exe";
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
    private static readonly TimeSpan s_previousExitWait = TimeSpan.FromSeconds(15);
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

    public static async Task<List<string>> NotesAsync(string tag)
    {
        try
        {
            using var release = await GetJsonAsync(AppInfo.ReleaseApiUrl + tag);
            var body = release.RootElement.TryGetProperty("body", out var text)
                ? text.GetString()
                : null;
            return
            [
                .. (body ?? string.Empty)
                    .Split('\n')
                    .Select(line => line.Trim().TrimStart('-', '*').Trim())
                    .Where(line => line.Length > 0)
                    .Distinct(),
            ];
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Notes Are Extra, The Update Works Without Them
            return [];
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(AppInfo.DisplayName);
        using var response = await s_checkClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // A Sandboxed Or Renamed Run Never Replaces An Exe
    public static bool CanInstall =>
        AppInfo.SandboxRoot == null
        && Path.GetFileName(Environment.ProcessPath)
            ?.StartsWith(ExecutablePrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static Task<string> DownloadAsync(
        ReleaseInfo release,
        IProgress<(long Received, long Total)> progress
    )
    {
        // Off The Window's Thread, So The Window Never Freezes
        return Task.Run(() => DownloadOnPoolAsync(release, progress));
    }

    private static async Task<string> DownloadOnPoolAsync(
        ReleaseInfo release,
        IProgress<(long Received, long Total)> progress
    )
    {
        var started = Stopwatch.StartNew();
        var targetPath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath)!,
            $"LetsClip_{release.Tag}.exe"
        );

        // A Name Only This Attempt Owns, Removed If It Fails
        var partialPath = $"{targetPath}.{Guid.NewGuid():N}.download";
        var expectedDigest = await ExpectedDigestAsync(release.Tag);

        // A Body Stalled For A Minute Is Given Up
        using var stall = new CancellationTokenSource(s_stallTimeout);
        try
        {
            using var response = await s_downloadClient.GetAsync(
                string.Format(AssetUrlFormat, release.Tag),
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

    private static async Task<string> ExpectedDigestAsync(string tag)
    {
        // GitHub Hashes Every Release Asset, Its API Lists The Hash
        string? digest = null;
        try
        {
            using var release = await GetJsonAsync(AppInfo.ReleaseApiUrl + tag);
            if (release.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (
                        asset.TryGetProperty("name", out var name)
                        && name.GetString() == $"LetsClip_{tag}.exe"
                        && asset.TryGetProperty("digest", out var value)
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
            new ProcessStartInfo(executablePath, $"{AfterUpdateArgument} {Environment.ProcessId}")
            {
                UseShellExecute = false,
            }
        );
    }

    public static void FinishPreviousVersion(string[] arguments)
    {
        var index = Array.IndexOf(arguments, AfterUpdateArgument);
        if (
            AppInfo.SandboxRoot != null
            || index < 0
            || index + 1 >= arguments.Length
            || !int.TryParse(arguments[index + 1], out var processId)
        )
        {
            return;
        }

        try
        {
            // Still Running After The Wait, Old Exes Stay
            using var previous = Process.GetProcessById(processId);
            if (!previous.WaitForExit(s_previousExitWait))
            {
                s_logger.Warn($"Found Previous Version {processId} Still Running");
                return;
            }
        }
        catch (ArgumentException)
        {
            // Already Gone
        }

        var oldPaths = Directory
            .EnumerateFiles(
                Path.GetDirectoryName(Environment.ProcessPath)!,
                $"{ExecutablePrefix}*.exe"
            )
            .Where(path =>
                !string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            );
        foreach (var oldPath in oldPaths)
        {
            try
            {
                File.Delete(oldPath);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A Locked Old Exe Is Left For Next Time
            }
        }
    }
}
