using System.Buffers;
using LetsClip.Data.Helpers;
using LetsClip.Services.Capture;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary
{
    public static async Task<string> RenameAsync(string path, string? title)
    {
        // The File Carries The Name, So Shared Copies Do Too
        var metadata =
            MetadataOf(path) ?? throw new InvalidDataException("The clip could not be read");
        var folder = Path.GetDirectoryName(path)!;
        var stem =
            ClipNames.FileSafe(title)
            ?? ClipNames.For(
                ClipNames.SourceOf(metadata.Game, metadata.Monitor),
                metadata.RecordedAt
            );

        // Only Its Own Name Changing Case Keeps The Same Path
        var target = string.Equals(
            stem,
            Path.GetFileNameWithoutExtension(path),
            StringComparison.OrdinalIgnoreCase
        )
            ? Path.Combine(folder, stem + Path.GetExtension(path))
            : ClipSaver.UniquePath(folder, stem);
        if (target != path)
        {
            var original = OwnedOriginalOf(path);
            await ReplaceWhileBusyAsync(() => File.Move(path, target));
            MoveDetails(path, target);
            if (original is not null)
            {
                MoveOriginal(original, target);
            }

            s_logger.Info($"Renamed {Path.GetFileName(path)} To {Path.GetFileName(target)}");
        }

        UpdateMetadata(target, stored => stored with { Title = title });
        return target;
    }

    public async Task NameFilesAsync()
    {
        // Clips Named Before Names Moved Into Files Get Theirs Once
        foreach (var path in ClipsIn(Folder))
        {
            var title = MetadataOf(path)?.Title;
            if (
                ClipNames.FileSafe(title) is { } stem
                && !Path.GetFileNameWithoutExtension(path)
                    .StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            )
            {
                try
                {
                    await RenameAsync(path, title);
                }
                catch (Exception exception)
                    when (exception
                            is IOException
                                or UnauthorizedAccessException
                                or InvalidDataException
                    )
                {
                    // In Use Now, The Next Start Names It
                    s_logger.Warn($"Failed To Name A Clip File {exception.GetType().Name}");
                }
            }
        }
    }

    public static async Task<(int Moved, int Failed)> MoveAllAsync(
        string from,
        string to,
        IProgress<(long Moved, long Total)> progress
    )
    {
        // Clips With Their Originals And Details, Then Exports, Never Overwriting
        var exportsFrom = Path.Combine(from, ExportsFolder);
        var exportsTo = Path.Combine(to, ExportsFolder);
        var total = BytesToMove(from);
        var done = 0L;
        void Moved(long bytes)
        {
            done += bytes;
            progress.Report((done, Math.Max(done, total)));
        }

        // Clips Saved Meanwhile Land In The Old Folder, Passes Repeat
        List<string> arrived = [];
        HashSet<string> stuck = [with(StringComparer.OrdinalIgnoreCase)];
        for (var pass = 0; pass < 3; pass++)
        {
            var waiting = ClipsIn(from).Where(clip => !stuck.Contains(clip)).ToList();
            foreach (var clip in waiting)
            {
                try
                {
                    var target = ClipSaver.UniquePath(to, Path.GetFileNameWithoutExtension(clip));
                    var original = OwnedOriginalOf(clip);
                    await MoveFileAsync(clip, target, Moved);
                    MoveDetails(clip, target);
                    if (original is not null)
                    {
                        await MoveFileAsync(original, OriginalPathOf(target), Moved);
                    }

                    arrived.Add(target);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    // In Use Or Unreadable, It Stays Where It Was
                    stuck.Add(clip);
                    s_logger.Warn($"Failed To Move A Clip {exception.GetType().Name}");
                }
            }
        }

        Dictionary<string, string> renamed = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var export in ClipsIn(exportsFrom, exports: true))
        {
            try
            {
                var target = ClipSaver.UniquePath(
                    exportsTo,
                    Path.GetFileNameWithoutExtension(export)
                );
                await MoveFileAsync(export, target, Moved);
                if (Path.GetFileName(target) != Path.GetFileName(export))
                {
                    renamed[Path.GetFileName(export)] = Path.GetFileName(target);
                }
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                stuck.Add(export);
                s_logger.Warn($"Failed To Move An Export {exception.GetType().Name}");
            }
        }

        // An Export Renamed On Arrival Stays Its Clip's Export
        foreach (var clip in renamed.Count > 0 ? arrived : [])
        {
            UpdateMetadata(
                clip,
                stored =>
                    stored.Export is { } name && renamed.TryGetValue(name, out var now)
                        ? stored with
                        {
                            Export = now,
                        }
                        : stored
            );
        }

        // Only Emptied Folders Go, Anything Else Stays
        RemoveEmptyFolder(Path.Combine(from, OriginalsFolder));
        RemoveEmptyFolder(Path.Combine(from, DetailsFolder));
        RemoveEmptyFolder(exportsFrom);
        RemoveEmptyFolder(from);
        s_logger.Info(
            $"Moved {arrived.Count} Clips And {Formatter.FormatSize(done)} To {to}, {stuck.Count} Stayed"
        );
        return (arrived.Count, stuck.Count);
    }

    public static long BytesToMove(string from)
    {
        // Clips, Their Kept Originals And The Exports
        return ClipsIn(from)
                .Sum(clip =>
                    new FileInfo(clip).Length
                    + (OwnedOriginalOf(clip) is { } kept ? new FileInfo(kept).Length : 0)
                )
            + ClipsIn(Path.Combine(from, ExportsFolder), exports: true)
                .Sum(export => new FileInfo(export).Length);
    }

    public static bool HasRoomFor(string to, string from, long bytes)
    {
        // The Same Drive Only Renames, Another Needs The Space
        try
        {
            var target = new DriveInfo(Path.GetFullPath(to));
            return string.Equals(
                    target.Name,
                    Path.GetPathRoot(Path.GetFullPath(from)),
                    StringComparison.OrdinalIgnoreCase
                )
                || target.AvailableFreeSpace > bytes + 100_000_000;
        }
        catch (Exception exception)
            when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Unknown Space Is Tried, A Failed File Then Stays
            return true;
        }
    }

    private static List<string> ClipsIn(string folder, bool exports = false)
    {
        // Finished Files Only, Partial Saves Never Move, Sound Exports Too
        return
        [
            .. (exports ? s_exportPatterns : ["*.mp4"])
                .SelectMany(pattern => FilesIn(folder, pattern))
                .Where(path =>
                    !Path.GetFileName(path).StartsWith(PartialPrefix, StringComparison.Ordinal)
                ),
        ];
    }

    private static List<string> FilesIn(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, pattern)] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Unreadable Folder Holds Nothing
            return [];
        }
    }

    private static async Task MoveFileAsync(string source, string target, Action<long> moved)
    {
        // The Same Drive Renames At Once, Another Copies With Progress
        var length = new FileInfo(source).Length;
        var folder = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (folder.Name == OriginalsFolder)
        {
            folder.Attributes |= FileAttributes.Hidden;
        }

        if (
            string.Equals(
                Path.GetPathRoot(Path.GetFullPath(source)),
                Path.GetPathRoot(Path.GetFullPath(target)),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            await ReplaceWhileBusyAsync(() => File.Move(source, target));
            moved(length);
            return;
        }

        var temporary = PartialPathIn(folder.FullName);
        try
        {
            var times = new FileInfo(source);
            await using (
                var input = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBuffer,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                )
            )
            await using (
                var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBuffer,
                    FileOptions.Asynchronous
                )
            )
            {
                // One Rented Buffer, A Folder Move Copies Many Files
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        moved(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            // The Same Times As Before, So Its Details Still Match
            File.SetCreationTimeUtc(temporary, times.CreationTimeUtc);
            File.SetLastWriteTimeUtc(temporary, times.LastWriteTimeUtc);
            File.Move(temporary, target);
            await ReplaceWhileBusyAsync(() => File.Delete(source));
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void MoveDetails(string from, string to)
    {
        // Its Details And Pictures Follow The File
        lock (s_metadataGate)
        {
            if (ReadStored(from) is { } stored)
            {
                WriteMetadata(to, stored);
                DeleteDetails(from);
            }
        }

        if (!Directory.Exists(AppInfo.ThumbnailsDirectory))
        {
            return;
        }

        var key = FilePaths.KeyOf(to);
        foreach (var picture in PicturesOf(FilePaths.KeyOf(from)).ToList())
        {
            try
            {
                File.Move(
                    picture,
                    Path.Combine(
                        AppInfo.ThumbnailsDirectory,
                        key + Path.GetFileName(picture)[FilePaths.KeyLength..]
                    ),
                    overwrite: true
                );
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A Missing Picture Is Made Again
            }
        }
    }

    private static void MoveOriginal(string original, string clip)
    {
        // Beside Its Clip's New Name, A Stale One Set Aside
        var target = OriginalPathOf(clip);
        if (File.Exists(target))
        {
            File.Move(
                target,
                ClipSaver.UniquePath(
                    Path.GetDirectoryName(target)!,
                    Path.GetFileNameWithoutExtension(target)
                )
            );
        }

        File.Move(original, target);
    }
}
