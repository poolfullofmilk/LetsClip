using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using NLog;

namespace LetsClip.Services.Media;

public sealed class ClipLibrary : IDisposable
{
    // Temporary Outputs Carry This Prefix Until They Are Finished
    public const string PartialPrefix = ".LetsClip-";

    // A Cut Clip's Full Original Waits Here, Hidden Beside It
    private const string OriginalsFolder = ".Originals";
    public const string ExportsFolder = "Exports";

    // Copies To Another Drive Move In Pieces This Big
    private const int CopyBuffer = 4 * 1024 * 1024;
    private const string ThumbnailSuffix = ".jpg";

    // Frames In A Strip, Each Twice The Timeline's Height
    public const int StripCells = 40;
    private const int StripHeight = 144;

    // A Picture Or Preview Reading The Clip Lets Go Soon
    private const int ReplaceAttempts = 5;

    private static readonly TimeSpan s_replaceRetry = TimeSpan.FromSeconds(1);
    private static readonly string s_stripSuffix = $".strip{StripCells}.jpg";

    // The Sound's Outline, Drawn Under The Strip Once Per File
    private const string WaveSuffix = ".wave.jpg";
    private const string WaveSize = "1600x64";
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly string s_metadataDirectory = Path.Combine(
        AppInfo.DataDirectory,
        "Clips"
    );
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(600);

    // A Lost Watcher Or Missing Drive Is Tried Again
    private static readonly TimeSpan s_rewatch = TimeSpan.FromSeconds(15);

    // Each Change Reads, Edits And Writes Alone
    private static readonly Lock s_metadataGate = new();

    private readonly SemaphoreSlim _probing = new(4, 4);
    private FileSystemWatcher? _watcher;
    private readonly Timer _changeTimer;
    private readonly Timer _rewatchTimer;
    private readonly CancellationTokenSource _closing = new();

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
            _ => Guard(Settle),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _rewatchTimer = new Timer(
            _ => Guard(Rewatch),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        Watch();
    }

    public event Action? Changed;

    // A Pass Ended, Waiting Tiles Settle
    public event Action? PicturesDone;

    // Each Picture Shows On Its Card Once Made
    public event Action<string, string>? PictureMade;

    public string Folder { get; private set; }

    // True While A Picture Pass Runs
    public bool IsMakingPictures => Volatile.Read(ref _filling) == 1;

    // Missing Or Unreadable, Never The Same As Empty
    public bool IsUnavailable { get; private set; }

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
            IsUnavailable = !Directory.Exists(Folder);
            paths = IsUnavailable ? [] : Directory.GetFiles(Folder, "*.mp4");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Unreadable Folder Lists Nothing, The Page Says So
            IsUnavailable = true;
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
                foreach (var clip in missing.TakeWhile(_ => !_closing.IsCancellationRequested))
                {
                    try
                    {
                        if (await ThumbnailAsync(clip.Path, clip.Duration) is { } picture)
                        {
                            PictureMade?.Invoke(clip.Path, picture);
                        }
                    }
                    catch (Exception exception)
                        when (exception
                                is IOException
                                    or UnauthorizedAccessException
                                    or TimeoutException
                        )
                    {
                        // One Bad Clip Keeps A Plain Tile, Others Go On
                        s_logger.Warn($"Failed To Make A Thumbnail {exception.GetType().Name}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _filling, 0);
                if (Interlocked.Exchange(ref _fillAgain, 0) == 1)
                {
                    Changed?.Invoke();
                }

                PicturesDone?.Invoke();
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

        var metadata = ReadMetadata(file) is { } known
            ? known.Width > 0
                ? known
                : await AddSizeAsync(file, known, token)
            : await ProbeMetadataAsync(file, token);
        if (metadata is null)
        {
            return null;
        }

        var thumbnail = PicturePathOf(path, ThumbnailSuffix);
        return new ClipInfo(
            file.FullName,
            Path.GetFileNameWithoutExtension(file.Name),
            metadata.RecordedAt,
            metadata.Duration,
            file.Length,
            File.Exists(thumbnail) ? thumbnail : null,
            metadata.Title,
            metadata.Game,
            !metadata.ForeignOriginal && File.Exists(OriginalPathOf(path)),
            metadata.Width,
            metadata.Height,
            metadata.FrameRate ?? 0,
            metadata.Monitor,
            file.LastWriteTimeUtc,
            metadata.IsEdited
        );
    }

    #region Names And Moves
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
        var moved = 0;
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

                    moved++;
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

        foreach (var export in ClipsIn(exportsFrom))
        {
            try
            {
                await MoveFileAsync(
                    export,
                    ClipSaver.UniquePath(exportsTo, Path.GetFileNameWithoutExtension(export)),
                    Moved
                );
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                stuck.Add(export);
                s_logger.Warn($"Failed To Move An Export {exception.GetType().Name}");
            }
        }

        // Only Emptied Folders Go, Anything Else Stays
        RemoveEmptyFolder(Path.Combine(from, OriginalsFolder));
        RemoveEmptyFolder(exportsFrom);
        RemoveEmptyFolder(from);
        s_logger.Info(
            $"Moved {moved} Clips And {Formatter.FormatSize(done)} To {to}, {stuck.Count} Stayed"
        );
        return (moved, stuck.Count);
    }

    public static long BytesToMove(string from)
    {
        // Clips, Their Kept Originals And The Exports
        return ClipsIn(from)
                .Sum(clip =>
                    new FileInfo(clip).Length
                    + (OwnedOriginalOf(clip) is { } kept ? new FileInfo(kept).Length : 0)
                )
            + ClipsIn(Path.Combine(from, ExportsFolder)).Sum(export => new FileInfo(export).Length);
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

    private static List<string> ClipsIn(string folder)
    {
        // Finished Files Only, A Partial Save Never Moves
        try
        {
            return Directory.Exists(folder)
                ?
                [
                    .. Directory
                        .EnumerateFiles(folder, "*.mp4")
                        .Where(path =>
                            !Path.GetFileName(path)
                                .StartsWith(PartialPrefix, StringComparison.Ordinal)
                        ),
                ]
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Unreadable Folder Holds Nothing To Move
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
                var buffer = new byte[CopyBuffer];
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    moved(read);
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
                TryDelete(MetadataPathOf(from));
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
    #endregion

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
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                output,
            ],
            TimeSpan.FromMinutes(2),
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

    private static async Task ReplaceWhileBusyAsync(Action replace)
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
        try
        {
            await ThumbnailAsync(path, duration);
        }
        catch (Exception exception)
            when (exception is IOException or TimeoutException or Win32Exception)
        {
            s_logger.Warn($"Failed To Make A Thumbnail {exception.GetType().Name}");
        }
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
            // Clips, Exports And Kept Originals All Count
            var used = new DirectoryInfo(folder)
                .EnumerateFiles("*.mp4", SearchOption.AllDirectories)
                .Sum(file => file.Length);
            return (used, new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown Space Only Hides A Row
            return null;
        }
    }

    private static Task<string?> ThumbnailAsync(string path, double duration)
    {
        var at = FFmpeg.Seconds(Math.Min(1, duration / 2));
        return RenderAsync(
            PicturePathOf(path, ThumbnailSuffix),
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
        // Keyframes Across The Clip, The Page Draws What Fits
        // Sparse Keyframes Repeat Their Last, So No Tile Stays Black
        var rate = FFmpeg.Seconds(StripCells / Math.Max(duration - 1, 0.5));
        return RenderAsync(
            PicturePathOf(path, s_stripSuffix),
            [
                "-skip_frame",
                "nokey",
                "-i",
                path,
                "-an",
                "-vf",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"fps={rate},tpad=stop={StripCells}:stop_mode=clone,scale=-2:{StripHeight},tile={StripCells}x1"
                ),
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
        try
        {
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. arguments, temporary],
                timeout,
                token
            );
            if (!result.Succeeded || !File.Exists(temporary))
            {
                return null;
            }

            File.Move(temporary, target, overwrite: true);
            DeleteOlderPictures(target);
            return target;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static Task<string?> EnsureWaveAsync(
        string path,
        double duration,
        CancellationToken token
    )
    {
        // White On Black, The Page Lays It Over The Strip
        return RenderAsync(
            PicturePathOf(path, WaveSuffix),
            [
                "-i",
                path,
                "-filter_complex",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[0:a:0]atrim=0:{FFmpeg.Seconds(duration)},showwavespic=s={WaveSize}:colors=white:scale=sqrt:draw=full:filter=peak"
                ),
                "-frames:v",
                "1",
                "-q:v",
                "3",
            ],
            TimeSpan.FromSeconds(60),
            token
        );
    }

    private static string KindOf(string picture)
    {
        return picture.EndsWith(s_stripSuffix, StringComparison.Ordinal) ? s_stripSuffix
            : picture.EndsWith(WaveSuffix, StringComparison.Ordinal) ? WaveSuffix
            : ThumbnailSuffix;
    }

    private static void DeleteOlderPictures(string target)
    {
        // Other Generations Of This Clip's Same Kind Of Picture
        var kind = KindOf(target);
        var name = Path.GetFileName(target);
        foreach (var older in PicturesOf(name[..FilePaths.KeyLength]))
        {
            if (older != target && KindOf(older) == kind)
            {
                TryDelete(older);
            }
        }
    }

    private static IEnumerable<string> PicturesOf(string key)
    {
        // Pictures A Running Render Still Writes Are Left Alone
        return Directory
            .EnumerateFiles(AppInfo.ThumbnailsDirectory, $"{key}*.jpg")
            .Where(file => !file.EndsWith(".tmp.jpg", StringComparison.Ordinal));
    }

    private static string PicturePathOf(string path, string suffix)
    {
        // Named By The File's Generation, Replaced Files Get New Ones
        var file = new FileInfo(path);
        var generation = file.Exists ? $"-{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}" : "";
        return Path.Combine(
            AppInfo.ThumbnailsDirectory,
            $"{FilePaths.KeyOf(path)}{generation}{suffix}"
        );
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

        TryDelete(MetadataPathOf(path));
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
        return
            ReadStored(file.FullName) is { } metadata
            && metadata.FileSize == file.Length
            && metadata.FileWriteTimeUtc == file.LastWriteTimeUtc
            && metadata.Duration > 0
            ? metadata
            : null;
    }

    private static ClipMetadata? ReadStored(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ClipMetadata>(File.ReadAllText(MetadataPathOf(path)));
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
        var target = MetadataPathOf(path);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            lock (s_metadataGate)
            {
                var file = new FileInfo(path);
                var stamped = metadata with
                {
                    FileSize = file.Length,
                    FileWriteTimeUtc = file.LastWriteTimeUtc,
                };
                Directory.CreateDirectory(s_metadataDirectory);
                File.WriteAllText(temporary, JsonSerializer.Serialize(stamped));
                File.Move(temporary, target, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Metadata Only Remembers The Trim, The Clip Itself Is Safe
            s_logger.Warn($"Failed To Save Clip Metadata {exception.GetType().Name}");
            TryDelete(temporary);
        }
    }

    private static string MetadataPathOf(string path)
    {
        return Path.Combine(s_metadataDirectory, FilePaths.KeyOf(path) + ".json");
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
            watcher.Error += (_, _) => OnWatcherLost();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception exception)
            when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Without A Watcher The Page Still Refreshes When Opened
            s_logger.Warn($"Failed To Watch Clips {exception.GetType().Name}");
            _rewatchTimer.Change(s_rewatch, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnWatcherLost()
    {
        // Overflowed Or Its Drive Left, Rescan And Watch Afresh
        Debounce(null);
        _rewatchTimer.Change(s_rewatch, Timeout.InfiniteTimeSpan);
    }

    private void Rewatch()
    {
        if (!_closing.IsCancellationRequested)
        {
            Watch();
            Changed?.Invoke();
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

    private static void Guard(Action body)
    {
        try
        {
            body();
        }
        catch (Exception exception)
        {
            // A Throwing Timer Ends LetsClip, The Next Change Tries Again
            s_logger.Warn($"Failed To Follow The Clips Folder {exception.GetType().Name}");
        }
    }

    private void Debounce(string? name)
    {
        if (name?.StartsWith(PartialPrefix, StringComparison.Ordinal) == true)
        {
            return;
        }

        try
        {
            _changeTimer.Change(s_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // A Change Seen While Closing Has Nobody To Tell
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _watcher?.Dispose();
        _changeTimer.Dispose();
        _rewatchTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
