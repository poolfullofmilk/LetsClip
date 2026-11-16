using System.Buffers;
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

    // Each Clip's Details, Hidden Beside It, So Moves Keep Them
    private const string DetailsFolder = ".Details";

    // Copies To Another Drive Move In Pieces This Big
    private const int CopyBuffer = 4 * 1024 * 1024;
    private const string ThumbnailSuffix = ".end.jpg";

    // Pictures From A Second In, Shown Until Remade
    private const string FirstSecondSuffix = ".jpg";

    // Frames In A Strip, Each Twice The Timeline's Height
    public const int StripCells = 40;
    private const int StripHeight = 144;

    // A Picture Or Preview Reading The Clip Lets Go Soon
    private const int ReplaceAttempts = 5;

    private static readonly TimeSpan s_replaceRetry = TimeSpan.FromSeconds(1);
    private static readonly string s_stripSuffix = $".strip{StripCells}.jpg";

    // The Sound's Outline, Drawn Under The Strip Once Per File
    private const string WaveSuffix = ".wave.jpg";

    // A Zoomed Timeline's Window, Only The Newest Kept Per Clip
    private const string ZoomMark = ".zoom-";
    private const string ZoomWaveMark = ".zoomwave-";
    private const string WaveSize = "1600x64";

    // Decoded Before A Zoomed Window, An Exact Seek Shifts
    private const double WaveLead = 2;

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // Where Details Lived Before They Moved Beside The Clips
    private static readonly string s_legacyDetailsDirectory = Path.Combine(
        AppInfo.DataDirectory,
        "Clips"
    );
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan s_timeTolerance = TimeSpan.FromSeconds(2);

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

    // Leftovers Of Clips Gone While Closed, Swept Once Per Start
    private int _sweptLeftovers;

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
        var folder = Folder;
        string[] paths;
        try
        {
            IsUnavailable = !Directory.Exists(folder);
            paths = IsUnavailable
                ? []
                :
                [
                    .. Directory
                        .GetFiles(folder, "*.mp4")
                        .Where(path =>
                            !Path.GetFileName(path)
                                .StartsWith(PartialPrefix, StringComparison.Ordinal)
                        ),
                ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An Unreadable Folder Lists Nothing, The Page Says So
            IsUnavailable = true;
            s_logger.Warn($"Failed To List Clips {exception.GetType().Name}");
            return [];
        }

        // Gathered Once, Only When A Clip Lacks Its Details
        var orphans = new Lazy<ConcurrentDictionary<(long Size, DateTime Written), string>>(() =>
            OrphanDetails(folder, paths)
        );
        var clips = await Task.WhenAll(paths.Select(path => GetAsync(path, token, orphans)));
        var listed = clips.OfType<ClipInfo>().OrderByDescending(clip => clip.RecordedAt).ToList();
        FillThumbnails([
            .. listed.Where(clip =>
                clip.ThumbnailPath?.EndsWith(ThumbnailSuffix, StringComparison.Ordinal) != true
            ),
        ]);
        if (!IsUnavailable && Interlocked.Exchange(ref _sweptLeftovers, 1) == 0)
        {
            _ = Task.Run(() => Guard(() => SweepLeftovers(folder, paths)), CancellationToken.None);
        }

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
                // One Bad Clip Keeps A Plain Tile, Others Go On
                foreach (var clip in missing.TakeWhile(_ => !_closing.IsCancellationRequested))
                {
                    if (await ThumbnailAsync(clip.Path, clip.Duration) is { } picture)
                    {
                        PictureMade?.Invoke(clip.Path, picture);
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

    public async Task<ClipInfo?> GetAsync(
        string path,
        CancellationToken token,
        Lazy<ConcurrentDictionary<(long Size, DateTime Written), string>>? orphans = null
    )
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
            : Adopt(file, orphans) ?? await ProbeMetadataAsync(file, token);
        if (metadata is null)
        {
            return null;
        }

        // Details Without A Source Take It From The Saved Name
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var (game, monitor) = ClipNames.SourceFrom(name);
        if (metadata is { Game: null, Monitor: null } && (game is not null || monitor is not null))
        {
            metadata = metadata with { Game = game, Monitor = monitor };
            UpdateMetadata(path, stored => stored with { Game = game, Monitor = monitor });
        }

        var thumbnail = PicturePathOf(path, ThumbnailSuffix);
        var firstSecond = PicturePathOf(path, FirstSecondSuffix);
        var export = ExportPathOf(path, metadata.Export);
        return new ClipInfo(
            file.FullName,
            name,
            metadata.RecordedAt,
            metadata.Duration,
            file.Length,
            File.Exists(thumbnail) ? thumbnail
                : File.Exists(firstSecond) ? firstSecond
                : null,
            metadata.Title,
            metadata.Game,
            !metadata.ForeignOriginal && File.Exists(OriginalPathOf(path)),
            metadata.Width,
            metadata.Height,
            metadata.FrameRate ?? 0,
            metadata.Monitor,
            file.LastWriteTimeUtc,
            metadata.IsEdited,
            File.Exists(export) ? export : null
        );
    }

    public static string? ExportPathOf(string clip, string? export)
    {
        // The Last Export Is Remembered By Its Name In Exports
        return export is null
            ? null
            : Path.Combine(Path.GetDirectoryName(clip) ?? "", ExportsFolder, export);
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
        foreach (var export in ClipsIn(exportsFrom))
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
        return
        [
            .. FilesIn(folder, "*.mp4")
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
            var used = new DirectoryInfo(folder)
                .EnumerateFiles(
                    "*.mp4",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.System,
                    }
                )
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
        // The Keyframe Two Seconds Before The Press, One Frame Decoded
        // Passed Through, So A Still Window's Last Frame Still Counts
        return RenderAsync(
            PicturePathOf(path, ThumbnailSuffix),
            [
                "-noaccurate_seek",
                "-skip_frame",
                "nokey",
                "-ss",
                FFmpeg.Seconds(Math.Max(duration / 2, duration - 2)),
                "-i",
                path,
                "-frames:v",
                "1",
                "-fps_mode",
                "passthrough",
                "-vf",
                "scale=480:-2",
                "-q:v",
                "4",
            ],
            TimeSpan.FromSeconds(30),
            CancellationToken.None
        );
    }

    public static Task<string?> EnsureStripAsync(
        string path,
        double duration,
        CancellationToken token,
        TrimRange? window = null
    )
    {
        // One Seek Per Cell, Twice As Fast As Decoding All
        var from = window?.Start ?? 0;
        var step = (window?.Length ?? Math.Max(duration - 1, 0.5)) / StripCells;
        var cells = Enumerable.Range(0, StripCells).ToList();
        var inputs = cells.SelectMany(cell =>
            new[]
            {
                "-threads",
                "1",
                "-noaccurate_seek",
                "-skip_frame",
                "nokey",
                "-ss",
                FFmpeg.Seconds(from + (cell * step)),
                "-i",
                path,
            }
        );
        var graph = string.Concat(
            cells.Select(cell =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{cell}:v]trim=end_frame=1,setpts=PTS-STARTPTS,scale=-2:{StripHeight}[c{cell}];"
                )
            )
        );
        var stack =
            string.Concat(cells.Select(cell => $"[c{cell}]")) + $"hstack=inputs={StripCells}";
        return RenderAsync(
            PicturePathOf(
                path,
                window is { } zoomed ? WindowSuffix(ZoomMark, zoomed) : s_stripSuffix
            ),
            [.. inputs, "-filter_complex", graph + stack, "-frames:v", "1", "-q:v", "5"],
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
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp.jpg";
        try
        {
            Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
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
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or TimeoutException
                        or Win32Exception
            )
        {
            // A Picture Only Decorates, Its Clip Shows Without It
            s_logger.Warn($"Failed To Make A Picture {exception.GetType().Name}");
            return null;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static Task<string?> EnsureWaveAsync(
        string path,
        double duration,
        CancellationToken token,
        TrimRange? window = null
    )
    {
        // White On Black, The Page Lays It Over The Strip
        var (from, to) = window is { } zoomed ? (zoomed.Start, zoomed.End) : (0, duration);

        // A Late Window Seeks Just Before, The Trim Stays Exact
        var seek = from > WaveLead ? from - WaveLead : 0;
        IReadOnlyList<string> input = seek > 0 ? ["-ss", FFmpeg.Seconds(seek)] : [];
        return RenderAsync(
            PicturePathOf(path, window is { } part ? WindowSuffix(ZoomWaveMark, part) : WaveSuffix),
            [
                .. input,
                "-i",
                path,
                "-filter_complex",
                $"[0:a:0]atrim={FFmpeg.Seconds(from - seek)}:{FFmpeg.Seconds(to - seek)},showwavespic=s={WaveSize}:colors=white:scale=sqrt:draw=full:filter=peak",
                "-frames:v",
                "1",
                "-q:v",
                "3",
            ],
            TimeSpan.FromSeconds(60),
            token
        );
    }

    private static string WindowSuffix(string mark, TrimRange window)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{mark}{window.Start:0.###}-{window.End:0.###}.jpg"
        );
    }

    private static string KindOf(string picture)
    {
        return picture.EndsWith(s_stripSuffix, StringComparison.Ordinal) ? s_stripSuffix
            : picture.EndsWith(WaveSuffix, StringComparison.Ordinal) ? WaveSuffix
            : picture.Contains(ZoomWaveMark, StringComparison.Ordinal) ? ZoomWaveMark
            : picture.Contains(ZoomMark, StringComparison.Ordinal) ? ZoomMark
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
