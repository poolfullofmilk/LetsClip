using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary
{
    public static ClipMetadata? MetadataOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? ReadMetadata(file) : null;
    }

    public static void UpdateMetadata(string path, Func<ClipMetadata, ClipMetadata> change)
    {
        lock (s_metadataGate)
        {
            if (MetadataOf(path) is { } metadata)
            {
                WriteMetadata(path, change(metadata));
            }
        }
    }

    private static void Forget(string path)
    {
        // A Deleted Clip Takes Its Pictures And Metadata With It
        if (Directory.Exists(AppInfo.ThumbnailsDirectory))
        {
            foreach (var picture in PicturesOf(FilePaths.KeyOf(path)))
            {
                TryDelete(picture);
            }
        }

        DeleteDetails(path);
    }

    private static void SweepLeftovers(string folder, string[] paths)
    {
        // Details And Pictures Of Clips Deleted While LetsClip Was Closed
        var kept = KeysOf(paths);
        var details = LeftDetails(folder, kept);
        var pictures = FilesIn(AppInfo.ThumbnailsDirectory, "*.jpg")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name =>
                name.Length > FilePaths.KeyLength
                && !name.EndsWith(".tmp.jpg", StringComparison.Ordinal)
                && !kept.Contains(name[..FilePaths.KeyLength])
            )
            .Select(name => Path.Combine(AppInfo.ThumbnailsDirectory, name))
            .ToList();
        foreach (var file in details.Concat(pictures))
        {
            TryDelete(file);
        }

        RemoveEmptyFolder(s_legacyDetailsDirectory);
        if (details.Count + pictures.Count > 0)
        {
            s_logger.Info(
                $"Swept {details.Count} Details And {pictures.Count} Pictures Of Clips Gone While LetsClip Was Closed"
            );
        }
    }

    private static ConcurrentDictionary<(long Size, DateTime Written), string> OrphanDetails(
        string folder,
        string[] paths
    )
    {
        var orphans = new ConcurrentDictionary<(long Size, DateTime Written), string>();
        foreach (var file in LeftDetails(folder, KeysOf(paths)))
        {
            if (ReadJson(file) is { } stored)
            {
                orphans.TryAdd((stored.FileSize, stored.FileWriteTimeUtc), file);
            }
        }

        return orphans;
    }

    private static HashSet<string> KeysOf(string[] paths)
    {
        return paths
            .Select(path => FilePaths.KeyOf(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> LeftDetails(string folder, HashSet<string> kept)
    {
        // Details Whose Clip Left, Here Or In The Older Folder
        return
        [
            .. FilesIn(Path.Combine(folder, DetailsFolder), "*.json")
                .Where(file =>
                    !File.Exists(Path.Combine(folder, Path.GetFileNameWithoutExtension(file)))
                )
                .Concat(
                    FilesIn(s_legacyDetailsDirectory, "*.json")
                        .Where(file => !kept.Contains(Path.GetFileNameWithoutExtension(file)))
                ),
        ];
    }

    private static ClipMetadata? Adopt(
        FileInfo file,
        Lazy<ConcurrentDictionary<(long Size, DateTime Written), string>>? orphans
    )
    {
        // Renamed Or Moved Outside LetsClip, Size And Time Find Details
        if (
            orphans?.Value.Keys.FirstOrDefault(left =>
                left.Size == file.Length && IsSameTime(left.Written, file.LastWriteTimeUtc)
            )
                is not { Size: > 0 } key
            || !orphans.Value.TryRemove(key, out var source)
            || ReadJson(source) is not { } stored
        )
        {
            return null;
        }

        WriteMetadata(file.FullName, stored);
        TryDelete(source);
        s_logger.Info($"Found The Details Of {file.Name} By Its Size And Time");
        return ReadMetadata(file);
    }

    private async Task<ClipMetadata?> ProbeMetadataAsync(FileInfo file, CancellationToken token)
    {
        // A Clip Without Metadata Gets It From The File, Once
        if (await ProbeOnceAsync(file, token) is not { } media)
        {
            return null;
        }

        // Generated Names Know Their Recording Time, Copies Do Not
        var metadata = new ClipMetadata(
            ClipNames.DateOf(Path.GetFileNameWithoutExtension(file.Name))
                ?? new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
            media.Duration,
            null,
            null,
            file.Length,
            file.LastWriteTimeUtc,
            (int)Math.Round(media.FrameRate),
            Width: media.Width,
            Height: media.Height,
            ForeignOriginal: File.Exists(OriginalPathOf(file.FullName))
        );
        WriteMetadata(file.FullName, metadata);
        return metadata;
    }

    private async Task<ClipMetadata> AddSizeAsync(
        FileInfo file,
        ClipMetadata metadata,
        CancellationToken token
    )
    {
        // Metadata From Before Sizes Were Kept Gets Them Once
        if (await ProbeOnceAsync(file, token) is not { } media)
        {
            return metadata;
        }

        UpdateMetadata(
            file.FullName,
            current => current with { Width = media.Width, Height = media.Height }
        );
        return metadata with { Width = media.Width, Height = media.Height };
    }

    private async Task<MediaInfo?> ProbeOnceAsync(FileInfo file, CancellationToken token)
    {
        await _probing.WaitAsync(token);
        try
        {
            var media = await MediaProbe.ProbeAsync(file.FullName, token);
            return media is { HasVideo: true, Duration: > 0 } ? media : null;
        }
        catch (Exception exception) when (exception is TimeoutException or Win32Exception)
        {
            // A Stuck Probe Leaves Out One New Clip, Never All
            s_logger.Warn($"Failed To Probe A Clip {exception.GetType().Name}");
            return null;
        }
        finally
        {
            _probing.Release();
        }
    }

    private static ClipMetadata? ReadMetadata(FileInfo file)
    {
        // Another File Under The Same Name Is Probed Again
        if (
            ReadStored(file.FullName) is not { Duration: > 0 } metadata
            || metadata.FileSize != file.Length
            || !IsSameTime(metadata.FileWriteTimeUtc, file.LastWriteTimeUtc)
        )
        {
            return null;
        }

        // From A Coarser Drive, Stamped Anew For Exact Reads
        if (metadata.FileWriteTimeUtc != file.LastWriteTimeUtc)
        {
            lock (s_metadataGate)
            {
                if (ReadStored(file.FullName) is { } current)
                {
                    WriteMetadata(file.FullName, current);
                }
            }
        }

        return metadata;
    }

    private static bool IsSameTime(DateTime stored, DateTime written)
    {
        // exFAT Keeps 10 ms, FAT32 And Zip Files Two Seconds
        return (stored - written).Duration() <= s_timeTolerance;
    }

    private static ClipMetadata? ReadStored(string path)
    {
        // Beside The Clip, Else Moved There From The Older Folder
        if (ReadJson(MetadataPathOf(path)) is { } stored)
        {
            return stored;
        }

        var legacy = LegacyPathOf(path);
        if (ReadJson(legacy) is not { } older)
        {
            return null;
        }

        if (WriteJson(MetadataPathOf(path), older))
        {
            TryDelete(legacy);
        }

        return older;
    }

    private static ClipMetadata? ReadJson(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<ClipMetadata>(File.ReadAllText(file));
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
        lock (s_metadataGate)
        {
            // A Clip Gone Meanwhile Has Nothing Left To Remember
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return;
            }

            var stamped = metadata with
            {
                FileSize = file.Length,
                FileWriteTimeUtc = file.LastWriteTimeUtc,
            };

            // A Folder Refusing Them Keeps Them In The Older Folder
            if (
                !WriteJson(MetadataPathOf(path), stamped) && !WriteJson(LegacyPathOf(path), stamped)
            )
            {
                // Metadata Only Remembers The Trim, The Clip Itself Is Safe
                s_logger.Warn($"Failed To Save Clip Metadata Of {file.Name}");
            }
        }
    }

    private static bool WriteJson(string target, ClipMetadata metadata)
    {
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            var folder = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (folder.Name == DetailsFolder)
            {
                folder.Attributes |= FileAttributes.Hidden;
            }

            File.WriteAllText(temporary, JsonSerializer.Serialize(metadata));
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return false;
        }
    }

    private static string MetadataPathOf(string path)
    {
        return Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            DetailsFolder,
            Path.GetFileName(path) + ".json"
        );
    }

    private static string LegacyPathOf(string path)
    {
        return Path.Combine(s_legacyDetailsDirectory, FilePaths.KeyOf(path) + ".json");
    }

    private static void DeleteDetails(string path)
    {
        TryDelete(MetadataPathOf(path));
        TryDelete(LegacyPathOf(path));
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
}
