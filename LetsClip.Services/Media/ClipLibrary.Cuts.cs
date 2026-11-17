using System.ComponentModel;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary
{
    public static string? OwnedOriginalOf(string path)
    {
        // A Replaced Clip's Old Backup Is Left Where It Is
        var original = OriginalPathOf(path);
        return File.Exists(original) && MetadataOf(path) is { ForeignOriginal: false }
            ? original
            : null;
    }

    public static string OriginalPathOf(string path)
    {
        return Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            OriginalsFolder,
            Path.GetFileName(path)
        );
    }

    public static async Task CutAsync(
        string path,
        TrimRange range,
        bool keepOriginal,
        CancellationToken token
    )
    {
        var metadata =
            MetadataOf(path) ?? throw new InvalidDataException("The clip could not be read");
        var temporary = PartialPathIn(Path.GetDirectoryName(path) ?? "");
        try
        {
            if (
                await CopyRangeAsync(path, range, ["-map", "0"], temporary, token)
                is not { HasVideo: true, Duration: > 0 } media
            )
            {
                throw new IOException("Failed To Cut The Clip");
            }

            // The First Cut Keeps The Full Clip If Asked
            var original = OwnedOriginalOf(path);
            var earlierStart = original is null ? 0 : metadata.CutStart ?? 0;
            await ReplaceWhileBusyAsync(() =>
            {
                if (keepOriginal && original is null)
                {
                    KeepFullClip(path, temporary);
                }
                else
                {
                    File.Move(temporary, path, overwrite: true);
                }
            });

            // Not Kept, An Earlier Original Goes Too, Leaving No History
            if (!keepOriginal && original is not null)
            {
                DeleteOriginal(original);
            }

            // Restore Needs The Cut In The Original's Own Time
            await ReplacedAsync(
                path,
                stored =>
                    stored with
                    {
                        TrimStart = null,
                        TrimEnd = null,
                        CutStart = keepOriginal ? earlierStart + range.Start : null,
                        CutEnd = keepOriginal ? earlierStart + range.End : null,
                        ForeignOriginal = false,
                        Cut = true,
                    },
                media.Duration
            );
            s_logger.Info(
                $"Cut {Path.GetFileName(path)} To {Formatter.FormatDuration(range.Start)} Through {Formatter.FormatDuration(range.End)}, {Formatter.FormatDuration(media.Duration)} Kept Losslessly"
            );
        }
        finally
        {
            await FFmpeg.DeleteOutputAsync(temporary);
        }
    }

    internal static async Task<MediaInfo?> CopyRangeAsync(
        string source,
        TrimRange range,
        IReadOnlyList<string> streams,
        string output,
        CancellationToken token
    )
    {
        // Stream Copied, The MP4 Edit List Hides The Keyframe Pre-Roll
        // A Hair Early, So The Start Frame Itself Is Kept
        var result = await FFmpeg.RunAsync(
            FFmpeg.FFmpegPath,
            [
                "-hide_banner",
                "-nostdin",
                "-loglevel",
                "error",
                "-y",
                "-ss",
                FFmpeg.Seconds(range.SeekStart),
                "-i",
                source,
                "-t",
                FFmpeg.Seconds(range.Length),
                .. streams,
                "-c",
                "copy",
                // A Still Window's Held Last Frame Ends At The Selection
                "-bsf:v",
                $"setts=duration=min(DURATION\\,max(1\\,{FFmpeg.Seconds(range.Length)}/TB-PTS))",
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                output,
            ],
            FFmpeg.CopyLimit(new FileInfo(source).Length),
            token
        );
        return result.Succeeded ? await MediaProbe.ProbeAsync(output, token) : null;
    }

    internal static string PartialPathIn(string folder)
    {
        // Unfinished Outputs Stay Out Of The Library Until Renamed
        return Path.Combine(folder, $"{PartialPrefix}{Guid.NewGuid():N}.mp4");
    }

    private static void DeleteOriginal(string original)
    {
        // The Cut Landed, A Locked Original Waits For Settings' Delete
        try
        {
            File.Delete(original);
            RemoveEmptyFolder(Path.GetDirectoryName(original)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            s_logger.Warn(
                $"Failed To Delete The Original Of {Path.GetFileName(original)}, {exception.GetType().Name}"
            );
        }
    }

    internal static async Task ReplaceWhileBusyAsync(Action replace)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                replace();
                return;
            }
            catch (Exception exception)
                when (attempt < ReplaceAttempts
                    && exception is IOException or UnauthorizedAccessException
                )
            {
                await Task.Delay(s_replaceRetry);
            }
        }
    }

    private static void KeepFullClip(string path, string temporary)
    {
        // A Stale Backup Is Set Aside, Never Overwritten
        var original = OriginalPathOf(path);
        var folder = Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        folder.Attributes |= FileAttributes.Hidden;
        if (File.Exists(original))
        {
            var stem = Path.GetFileNameWithoutExtension(original);
            File.Move(original, ClipSaver.UniquePath(folder.FullName, stem));
        }

        // One Step, The Source Always Keeps A Name
        try
        {
            File.Replace(temporary, path, original);
        }
        catch (IOException) when (!File.Exists(path) && File.Exists(original))
        {
            File.Move(original, path);
            throw;
        }
    }

    public static async Task RestoreAsync(string path)
    {
        // The Full Clip Comes Back, Its Handles On The Cut
        if (OwnedOriginalOf(path) is not { } original || MetadataOf(path) is not { } metadata)
        {
            throw new InvalidDataException("This clip has no original of its own");
        }

        await ReplaceWhileBusyAsync(() => File.Move(original, path, overwrite: true));
        RemoveEmptyFolder(Path.GetDirectoryName(original)!);

        // Past Here The Clip Is Back, Only Its Details Follow
        MediaInfo? media = null;
        try
        {
            media = await MediaProbe.ProbeAsync(path, CancellationToken.None);
        }
        catch (Exception exception) when (exception is TimeoutException or Win32Exception)
        {
            s_logger.Warn($"Failed To Probe A Restored Clip {exception.GetType().Name}");
        }

        await ReplacedAsync(
            path,
            stored =>
                stored with
                {
                    TrimStart = stored.CutStart,
                    TrimEnd = stored.CutEnd,
                    CutStart = null,
                    CutEnd = null,
                    Cut = false,
                },
            media?.Duration ?? metadata.Duration
        );
        s_logger.Info($"Restored The Original Of {Path.GetFileName(path)}");
    }

    public static long OriginalsSizeOf(string folder)
    {
        // Every Kept Original, Stale Ones Set Aside Too
        return OriginalsIn(folder).Sum(file => file.Length);
    }

    public static long DeleteOriginals(string folder)
    {
        // Cut Clips Stay, Only Their Way Back Goes
        var freed = 0L;
        foreach (var original in OriginalsIn(folder))
        {
            try
            {
                var length = original.Length;
                original.Delete();
                freed += length;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // One In Use Stays, The Next Delete Takes It
                s_logger.Warn($"Failed To Delete An Original {exception.GetType().Name}");
            }
        }

        RemoveEmptyFolder(Path.Combine(folder, OriginalsFolder));
        s_logger.Info($"Deleted The Kept Originals, {Formatter.FormatSize(freed)} Freed");
        return freed;
    }

    private static List<FileInfo> OriginalsIn(string folder)
    {
        var originals = new DirectoryInfo(Path.Combine(folder, OriginalsFolder));
        try
        {
            return originals.Exists ? [.. originals.EnumerateFiles("*.mp4")] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Unreadable Folder Counts As None
            return [];
        }
    }

    private static void RemoveEmptyFolder(string folder)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Empty Hidden Folder Harms Nothing
        }
    }

    private static async Task ReplacedAsync(
        string path,
        Func<ClipMetadata, ClipMetadata> change,
        double duration
    )
    {
        // Applied To What Is Stored Now, A Rename Meanwhile Survives
        lock (s_metadataGate)
        {
            if (ReadStored(path) is { } stored)
            {
                WriteMetadata(path, change(stored) with { Duration = duration });
            }
        }

        // The File Is Already Replaced, A Picture Only Decorates It
        await ThumbnailAsync(path, duration);
    }

    public static bool CanWriteTo(string folder)
    {
        // Made If Needed, One Throwaway File Proves It
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $"{PartialPrefix}{Guid.NewGuid():N}.probe");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static (long Used, long Free)? SpaceOf(string folder)
    {
        try
        {
            // Clips, Exports And Originals, A Root's System Folders Skipped
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.System,
            };
            var used = s_exportPatterns.Sum(pattern =>
                new DirectoryInfo(folder).EnumerateFiles(pattern, options).Sum(file => file.Length)
            );
            return (used, new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown Space Only Hides A Row
            return null;
        }
    }
}
