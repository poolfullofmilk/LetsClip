using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Media;

public sealed class ClipLibrary : IDisposable
{
    // Temporary Outputs Carry This Prefix Until They Are Finished
    public const string PartialPrefix = ".LetsClip-";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly string s_metadataDirectory = Path.Combine(
        AppInfo.DataDirectory,
        "Clips"
    );
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(600);

    private readonly SemaphoreSlim _probing = new(4, 4);
    private FileSystemWatcher? _watcher;
    private Timer? _changeTimer;
    private int _filling;

    public ClipLibrary(string folder)
    {
        Folder = folder;
        Watch();
    }

    public event Action? Changed;

    public string Folder { get; private set; }

    public void SetFolder(string folder)
    {
        if (string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Folder = folder;
        Watch();
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<ClipInfo>> ListAsync(CancellationToken token)
    {
        string[] paths;
        try
        {
            paths = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.mp4") : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Unreadable Folder Lists Nothing, The Page Shows Where
            s_logger.Warn($"Failed To List Clips {exception.GetType().Name}");
            return [];
        }

        var clips = await Task.WhenAll(
            paths
                .Where(path =>
                    !Path.GetFileName(path).StartsWith(PartialPrefix, StringComparison.Ordinal)
                )
                .Select(path => GetAsync(path, token))
        );
        var listed = clips.OfType<ClipInfo>().OrderByDescending(clip => clip.RecordedAt).ToList();
        FillThumbnails([.. listed.Where(clip => clip.ThumbnailPath is null)]);
        return listed;
    }

    private void FillThumbnails(List<ClipInfo> missing)
    {
        // Clips Copied In Get Pictures In The Background, One Pass
        if (missing.Count == 0 || Interlocked.Exchange(ref _filling, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var made = 0;
                foreach (var clip in missing)
                {
                    var metadata = MetadataOf(clip.Path);
                    var at = (metadata?.KeepStart ?? 0) + Math.Min(1, clip.Duration / 2);
                    if (
                        await EnsureThumbnailAsync(clip.Path, at, CancellationToken.None)
                        is not null
                    )
                    {
                        made++;
                    }
                }

                if (made > 0)
                {
                    Changed?.Invoke();
                }
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // A Missing Picture Only Leaves A Plain Tile
                s_logger.Warn($"Failed To Make Thumbnails {exception.GetType().Name}");
            }
            finally
            {
                Interlocked.Exchange(ref _filling, 0);
            }
        });
    }

    public async Task<ClipInfo?> GetAsync(string path, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        var metadata = ReadMetadata(file) ?? await ProbeMetadataAsync(file, token);
        if (metadata is null)
        {
            return null;
        }

        var thumbnail = ThumbnailPathOf(path);
        return new ClipInfo(
            file.FullName,
            Path.GetFileNameWithoutExtension(file.Name),
            metadata.RecordedAt,
            metadata.KeepEnd - metadata.KeepStart,
            file.Length,
            File.Exists(thumbnail) ? thumbnail : null,
            metadata.HasAudio,
            metadata.Width,
            metadata.Height,
            metadata.FrameRate
        );
    }

    public static ClipMetadata? MetadataOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? ReadMetadata(file) : null;
    }

    public static void Register(string path, ClipMetadata metadata)
    {
        WriteMetadata(path, metadata);
    }

    public static void SaveTrim(string path, TrimRange? range)
    {
        if (MetadataOf(path) is { } metadata)
        {
            WriteMetadata(path, metadata with { TrimStart = range?.Start, TrimEnd = range?.End });
        }
    }

    public static async Task<string?> EnsureThumbnailAsync(
        string path,
        double at,
        CancellationToken token
    )
    {
        var thumbnail = ThumbnailPathOf(path);
        if (File.Exists(thumbnail))
        {
            return thumbnail;
        }

        Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
        var temporary = thumbnail + ".tmp.jpg";
        var result = await FFmpeg.RunAsync(
            FFmpeg.FFmpegPath,
            [
                "-hide_banner",
                "-nostdin",
                "-loglevel",
                "error",
                "-y",
                "-ss",
                Seconds(at),
                "-i",
                path,
                "-frames:v",
                "1",
                "-vf",
                "scale=480:-2",
                "-q:v",
                "4",
                temporary,
            ],
            TimeSpan.FromSeconds(30),
            token
        );
        if (!result.Succeeded || !File.Exists(temporary))
        {
            TryDelete(temporary);
            return null;
        }

        File.Move(temporary, thumbnail, overwrite: true);
        return thumbnail;
    }

    public static async Task<string?> EnsureStripAsync(
        string path,
        double duration,
        CancellationToken token
    )
    {
        // Twelve Frames Across The Clip, One Picture For The Timeline
        var strip = Path.ChangeExtension(ThumbnailPathOf(path), ".strip.jpg");
        if (File.Exists(strip))
        {
            return strip;
        }

        Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
        var temporary = strip + ".tmp.jpg";
        // Keyframes Only, So The Last Second Fills No Tile
        var rate = Seconds(12 / Math.Max(duration - 1, 0.5));
        var result = await FFmpeg.RunAsync(
            FFmpeg.FFmpegPath,
            [
                "-hide_banner",
                "-nostdin",
                "-loglevel",
                "error",
                "-y",
                "-skip_frame",
                "nokey",
                "-i",
                path,
                "-an",
                "-vf",
                $"fps={rate},scale=-2:96,tile=12x1",
                "-frames:v",
                "1",
                "-fps_mode",
                "passthrough",
                "-q:v",
                "5",
                temporary,
            ],
            TimeSpan.FromSeconds(60),
            token
        );
        if (!result.Succeeded || !File.Exists(temporary))
        {
            TryDelete(temporary);
            return null;
        }

        File.Move(temporary, strip, overwrite: true);
        return strip;
    }

    public static string ThumbnailPathOf(string path)
    {
        return Path.Combine(AppInfo.ThumbnailsDirectory, KeyOf(path) + ".jpg");
    }

    private async Task<ClipMetadata?> ProbeMetadataAsync(FileInfo file, CancellationToken token)
    {
        // A Clip Without Metadata Gets It From The File, Once
        await _probing.WaitAsync(token);
        try
        {
            var media = await MediaProbe.ProbeAsync(file.FullName, token);
            if (media is not { HasVideo: true, Duration: > 0 })
            {
                return null;
            }

            var metadata = new ClipMetadata(
                new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                null,
                media.Duration,
                0,
                media.Duration,
                null,
                null,
                media.HasAudio,
                media.Width,
                media.Height,
                media.FrameRate,
                file.Length,
                file.LastWriteTimeUtc
            );
            WriteMetadata(file.FullName, metadata);
            return metadata;
        }
        finally
        {
            _probing.Release();
        }
    }

    private static ClipMetadata? ReadMetadata(FileInfo file)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<ClipMetadata>(
                File.ReadAllText(MetadataPathOf(file.FullName))
            );

            // Another File Under The Same Name Is Probed Again
            return
                metadata is not null
                && metadata.FileSize == file.Length
                && metadata.FileWriteTimeUtc == file.LastWriteTimeUtc
                && metadata.KeepEnd > metadata.KeepStart
                ? metadata
                : null;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing Or Damaged Metadata Means Probing The File
            return null;
        }
    }

    private static void WriteMetadata(string path, ClipMetadata metadata)
    {
        try
        {
            var file = new FileInfo(path);
            var stamped = metadata with
            {
                FileSize = file.Length,
                FileWriteTimeUtc = file.LastWriteTimeUtc,
            };
            Directory.CreateDirectory(s_metadataDirectory);
            var target = MetadataPathOf(path);
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stamped, s_json));
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Metadata Only Remembers The Trim, The Clip Itself Is Safe
            s_logger.Warn($"Failed To Save Clip Metadata {exception.GetType().Name}");
        }
    }

    private static string MetadataPathOf(string path)
    {
        return Path.Combine(s_metadataDirectory, KeyOf(path) + ".json");
    }

    private static string KeyOf(string path)
    {
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    private static string Seconds(double value)
    {
        return Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A Leftover Thumbnail Is Rewritten Next Time
        }
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(Folder))
        {
            return;
        }

        try
        {
            // Clips Moved Or Deleted In Explorer Refresh The Library
            var watcher = new FileSystemWatcher(Folder, "*.mp4")
            {
                NotifyFilter =
                    NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, _) => Debounce();
            watcher.Deleted += (_, _) => Debounce();
            watcher.Renamed += (_, _) => Debounce();
            watcher.Changed += (_, _) => Debounce();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            // Without A Watcher The Page Still Refreshes When Opened
            s_logger.Warn($"Failed To Watch Clips {exception.GetType().Name}");
        }
    }

    private void Debounce()
    {
        _changeTimer?.Dispose();
        _changeTimer = new Timer(
            _ => Changed?.Invoke(),
            null,
            s_debounce,
            Timeout.InfiniteTimeSpan
        );
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _changeTimer?.Dispose();
        _probing.Dispose();
        GC.SuppressFinalize(this);
    }
}
