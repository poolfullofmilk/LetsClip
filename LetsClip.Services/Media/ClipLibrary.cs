using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Media;

public sealed class ClipLibrary : IDisposable
{
    // Temporary Outputs Carry This Prefix Until They Are Finished
    public const string PartialPrefix = ".LetsClip-";

    // A Cut Clip's Full Original Waits Here, Hidden Beside It
    private const string OriginalsFolder = ".Originals";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly string s_metadataDirectory = Path.Combine(
        AppInfo.DataDirectory,
        "Clips"
    );
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(600);

    private readonly SemaphoreSlim _probing = new(4, 4);
    private FileSystemWatcher? _watcher;
    private readonly Timer _changeTimer;

    // Paths Gone From The Folder, Forgotten Once Changes Settle
    private readonly ConcurrentDictionary<string, byte> _gone = new(
        StringComparer.OrdinalIgnoreCase
    );
    private int _filling;
    private int _fillAgain;

    public ClipLibrary(string folder)
    {
        Folder = folder;
        _changeTimer = new Timer(
            _ => Settle(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        Watch();
    }

    public event Action? Changed;

    public string Folder { get; private set; }

    public void SetFolder(string folder)
    {
        if (FilePaths.AreSame(folder, Folder))
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
        if (missing.Count == 0)
        {
            return;
        }

        // A Clip Arriving Mid Pass Gets The Next One
        if (Interlocked.Exchange(ref _filling, 1) == 1)
        {
            Volatile.Write(ref _fillAgain, 1);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var made = 0;
                foreach (var clip in missing)
                {
                    var at = FFmpeg.Seconds(Math.Min(1, clip.Duration / 2));
                    if (await ThumbnailAsync(clip.Path, at) is not null)
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
                if (Interlocked.Exchange(ref _fillAgain, 0) == 1)
                {
                    Changed?.Invoke();
                }
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

        // Older Metadata Lacks A Size, Probed Once Keeping Its Trim
        var known = ReadMetadata(file);
        var metadata = known is { Width: > 0 }
            ? known
            : await ProbeMetadataAsync(file, known, token);
        if (metadata is null)
        {
            return null;
        }

        var thumbnail = ThumbnailPathOf(path);
        return new ClipInfo(
            file.FullName,
            Path.GetFileNameWithoutExtension(file.Name),
            metadata.RecordedAt,
            metadata.Duration,
            file.Length,
            File.Exists(thumbnail) ? thumbnail : null,
            metadata.Width,
            metadata.Height,
            metadata.FrameRate ?? 0,
            metadata.Title,
            metadata.Game,
            File.Exists(OriginalPathOf(path))
        );
    }

    public static ClipMetadata? MetadataOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? ReadMetadata(file) : null;
    }

    public static void UpdateMetadata(string path, Func<ClipMetadata, ClipMetadata> change)
    {
        if (MetadataOf(path) is { } metadata)
        {
            WriteMetadata(path, change(metadata));
        }
    }

    public static string OriginalPathOf(string path)
    {
        return Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            OriginalsFolder,
            Path.GetFileName(path)
        );
    }

    public static async Task CutAsync(string path, TrimRange range, CancellationToken token)
    {
        // Stream Copied, The MP4 Edit List Hides The Keyframe Pre-Roll
        var metadata =
            MetadataOf(path) ?? throw new InvalidDataException("The clip could not be read");
        var temporary = Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            $"{PartialPrefix}{Guid.NewGuid():N}.mp4"
        );
        try
        {
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-loglevel",
                    "error",
                    "-y",
                    "-ss",
                    FFmpeg.Seconds(range.Start),
                    "-i",
                    path,
                    "-t",
                    FFmpeg.Seconds(range.Length),
                    "-map",
                    "0",
                    "-c",
                    "copy",
                    "-movflags",
                    "+faststart",
                    "-f",
                    "mp4",
                    temporary,
                ],
                TimeSpan.FromMinutes(2),
                token
            );
            var media = result.Succeeded ? await MediaProbe.ProbeAsync(temporary, token) : null;
            if (media is not { HasVideo: true, Duration: > 0 })
            {
                throw new IOException($"Failed To Cut The Clip, {result.LastError}");
            }

            // The First Cut Keeps The Full Clip, Later Ones Replace
            var original = OriginalPathOf(path);
            if (!File.Exists(original))
            {
                var folder = Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                folder.Attributes |= FileAttributes.Hidden;
                File.Move(path, original);
            }

            File.Move(temporary, path, overwrite: true);
            Replaced(path, metadata, media.Duration);
            s_logger.Info(
                $"Cut {Path.GetFileName(path)} To {Formatter.FormatDuration(range.Start)} Through {Formatter.FormatDuration(range.End)}, {Formatter.FormatDuration(media.Duration)} Kept Losslessly"
            );
        }
        finally
        {
            await FFmpeg.DeleteOutputAsync(temporary);
        }
    }

    public static async Task RestoreAsync(string path)
    {
        // The Full Clip Comes Back, The Cut Is Dropped
        var original = OriginalPathOf(path);
        var metadata = MetadataOf(path);
        if (!File.Exists(original) || metadata is null)
        {
            return;
        }

        File.Move(original, path, overwrite: true);
        if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(original)!).Any())
        {
            Directory.Delete(Path.GetDirectoryName(original)!);
        }

        var media = await MediaProbe.ProbeAsync(path, CancellationToken.None);
        Replaced(path, metadata, media?.Duration ?? metadata.Duration);
        s_logger.Info($"Restored The Original Of {Path.GetFileName(path)}");
    }

    private static void Replaced(string path, ClipMetadata metadata, double duration)
    {
        // New Pictures, The Name, Game And Date Stay
        TryDelete(ThumbnailPathOf(path));
        TryDelete(StripPathOf(path));
        WriteMetadata(
            path,
            metadata with
            {
                Duration = duration,
                TrimStart = null,
                TrimEnd = null,
            }
        );
    }

    private static Task<string?> ThumbnailAsync(string path, string at)
    {
        return RenderAsync(
            ThumbnailPathOf(path),
            ["-ss", at, "-i", path, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4"],
            TimeSpan.FromSeconds(30),
            CancellationToken.None
        );
    }

    public static Task<string?> EnsureStripAsync(
        string path,
        double duration,
        CancellationToken token
    )
    {
        // Twelve Keyframes Across The Clip, The Last Second Skipped
        var rate = FFmpeg.Seconds(12 / Math.Max(duration - 1, 0.5));
        return RenderAsync(
            StripPathOf(path),
            [
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
            ],
            TimeSpan.FromSeconds(60),
            token
        );
    }

    private static async Task<string?> RenderAsync(
        string target,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken token
    )
    {
        if (File.Exists(target))
        {
            return target;
        }

        // Each Run Writes Its Own Temporary File
        Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp.jpg";
        var result = await FFmpeg.RunAsync(
            FFmpeg.FFmpegPath,
            ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. arguments, temporary],
            timeout,
            token
        );
        if (!result.Succeeded || !File.Exists(temporary))
        {
            TryDelete(temporary);
            return null;
        }

        File.Move(temporary, target, overwrite: true);
        return target;
    }

    public static string ThumbnailPathOf(string path)
    {
        return Path.Combine(AppInfo.ThumbnailsDirectory, KeyOf(path) + ".jpg");
    }

    private static string StripPathOf(string path)
    {
        return Path.ChangeExtension(ThumbnailPathOf(path), ".strip.jpg");
    }

    private static void Forget(string path)
    {
        // A Deleted Clip Takes Its Pictures And Metadata With It
        TryDelete(ThumbnailPathOf(path));
        TryDelete(StripPathOf(path));
        TryDelete(MetadataPathOf(path));
    }

    private async Task<ClipMetadata?> ProbeMetadataAsync(
        FileInfo file,
        ClipMetadata? known,
        CancellationToken token
    )
    {
        // A Clip Without Metadata Gets It From The File, Once
        await _probing.WaitAsync(token);
        try
        {
            MediaInfo? media;
            try
            {
                media = await MediaProbe.ProbeAsync(file.FullName, token);
            }
            catch (Exception exception) when (exception is TimeoutException or Win32Exception)
            {
                // A Stuck Probe Leaves Out One New Clip, Never All
                s_logger.Warn($"Failed To Probe A Clip {exception.GetType().Name}");
                return known;
            }

            if (media is not { HasVideo: true, Duration: > 0 })
            {
                return known;
            }

            // Generated Names Know Their Recording Time, Copies Do Not
            var metadata =
                known
                ?? new ClipMetadata(
                    ClipNames.DateOf(Path.GetFileNameWithoutExtension(file.Name))
                        ?? new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                    media.Duration,
                    null,
                    null,
                    file.Length,
                    file.LastWriteTimeUtc
                );
            metadata = metadata with
            {
                FrameRate = metadata.FrameRate ?? (int)Math.Round(media.FrameRate),
                Width = media.Width,
                Height = media.Height,
            };
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
                && metadata.Duration > 0
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

    public static void WriteMetadata(string path, ClipMetadata metadata)
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(stamped));
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
        try
        {
            // A First Run Has No Clips Folder Yet
            Directory.CreateDirectory(Folder);

            // Explorer Changes Refresh The Library, Temporary Saves Do Not
            var watcher = new FileSystemWatcher(Folder, "*.mp4")
            {
                NotifyFilter =
                    NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, change) => Debounce(change.Name);
            watcher.Deleted += (_, change) =>
            {
                _gone[change.FullPath] = 0;
                Debounce(change.Name);
            };
            watcher.Renamed += (_, change) =>
            {
                _gone[change.OldFullPath] = 0;
                Debounce(change.Name);
            };
            watcher.Changed += (_, change) => Debounce(change.Name);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception exception)
            when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Without A Watcher The Page Still Refreshes When Opened
            s_logger.Warn($"Failed To Watch Clips {exception.GetType().Name}");
        }
    }

    private void Settle()
    {
        // A Cut Moves Files Away And Back, Missing Ones Go
        foreach (var path in _gone.Keys)
        {
            _gone.TryRemove(path, out _);
            if (!File.Exists(path))
            {
                Forget(path);
            }
        }

        Changed?.Invoke();
    }

    private void Debounce(string? name)
    {
        if (name?.StartsWith(PartialPrefix, StringComparison.Ordinal) != true)
        {
            _changeTimer.Change(s_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _changeTimer.Dispose();
        _probing.Dispose();
        GC.SuppressFinalize(this);
    }
}
