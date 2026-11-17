using System.Collections.Concurrent;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary : IDisposable
{
    // Temporary Outputs Carry This Prefix Until They Are Finished
    public const string PartialPrefix = ".LetsClip-";

    // A Cut Clip's Full Original Waits Here, Hidden Beside It
    private const string OriginalsFolder = ".Originals";
    public const string ExportsFolder = "Exports";

    // Sound Only Exports Are .m4a Copies And .ogg Opus
    private static readonly string[] s_exportPatterns = ["*.mp4", "*.m4a", "*.ogg"];

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
    private const string WaveSuffix = ".waves.jpg";

    // A Zoomed Timeline's Window, Only The Newest Kept Per Clip
    private const string ZoomMark = ".zoom-";
    private const string ZoomWaveMark = ".zoomwaves-";
    private const string WaveSize = "1600x64";

    // The Accent's Light Blue, The Microphone Apart From The Game
    private const string VoiceColor = "0xb1d1fe";

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
