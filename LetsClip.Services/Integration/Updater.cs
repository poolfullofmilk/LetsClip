using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Integration;

public static partial class Updater
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

            // Signed Is Not Tagged, An Older Exe Is Refused
            if (!IsSameVersion(VersionOf(partialPath), release.Version))
            {
                throw new InvalidDataException($"Wrong Version Inside {release.Tag}");
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

    private static bool IsSameVersion(Version file, Version tag)
    {
        // 2.0.14.0 Against 2.0.14 Or 2.1, Missing Parts Zero
        return file.Major == tag.Major
            && file.Minor == tag.Minor
            && Math.Max(file.Build, 0) == Math.Max(tag.Build, 0)
            && Math.Max(file.Revision, 0) == Math.Max(tag.Revision, 0);
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
            && IsSameVersion(new Version(2, 0, 14, 0), new Version(2, 0, 14))
            && IsSameVersion(new Version(2, 1, 0, 0), new Version(2, 1))
            && !IsSameVersion(new Version(2, 0, 13, 0), new Version(2, 0, 14))
            && !IsSameVersion(new Version(2, 0, 14, 0), new Version(2, 1))
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
}
