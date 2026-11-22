using System.Collections.Concurrent;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary : IDisposable
{
    // Temporary Outputs Carry This Prefix Until They Are Finished
    private const string PartialPrefix = ".LetsClip-";

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
    private const string WaveSuffix = ".sound.jpg";

    // A Zoomed Timeline's Window, Only The Newest Kept Per Clip
    private const string ZoomMark = ".zoom-";
    private const string ZoomWaveMark = ".zoomsound-";
    private const string WaveSize = "1600x64";

    // A Darker Grey, The Microphone Apart From The Game
    private const string VoiceColor = "0x999999";

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

    // Pictures Waiting, Four Painters, The Cards Shown First
    private const int Painters = 4;
    private readonly Lock _picturesGate = new();
    private readonly List<ClipInfo> _waitingPictures = [];
    private int _painters;
    private volatile HashSet<string> _shownFirst = [];

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
    public bool IsMakingPictures
    {
        get
        {
            lock (_picturesGate)
            {
                return _painters > 0;
            }
        }
    }

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
        FileInfo[] files;
        try
        {
            // Each Listed File Carries Its Size And Time Already
            IsUnavailable = !Directory.Exists(folder);
            files = IsUnavailable
                ? []
                :
                [
                    .. new DirectoryInfo(folder)
                        .EnumerateFiles("*.mp4")
                        .Where(file =>
                            !file.Name.StartsWith(PartialPrefix, StringComparison.Ordinal)
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
        var paths = files.Select(file => file.FullName).ToArray();
        var orphans = new Lazy<ConcurrentDictionary<(long Size, DateTime Written), string>>(() =>
            OrphanDetails(folder, paths)
        );

        // One Look At Each Folder, Never One Per Clip
        var present = (
            Pictures: NamesIn(AppInfo.ThumbnailsDirectory),
            Originals: NamesIn(Path.Combine(folder, OriginalsFolder))
        );
        var clips = await Task.WhenAll(
            files.Select(file => GetAsync(file, token, orphans, present))
        );
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

    public void ShowFirst(IEnumerable<string> paths)
    {
        // The Cards On Screen Get Their Pictures Before The Rest
        _shownFirst = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void FillThumbnails(List<ClipInfo> missing)
    {
        // Pictures Come In The Background, Arrivals Join The Line
        int starting;
        lock (_picturesGate)
        {
            _waitingPictures.AddRange(
                missing.Where(clip =>
                    _waitingPictures.TrueForAll(waiting => waiting.Path != clip.Path)
                )
            );
            starting = _waitingPictures.Count == 0 ? 0 : Painters - _painters;
            _painters += starting;
        }

        for (var painter = 0; painter < starting; painter++)
        {
            _ = Task.Run(PaintAsync);
        }
    }

    private async Task PaintAsync()
    {
        // One Bad Clip Keeps A Plain Tile, Others Go On
        while (!_closing.IsCancellationRequested && NextPicture() is { } clip)
        {
            if (await ThumbnailAsync(clip.Path, clip.Duration) is { } picture)
            {
                PictureMade?.Invoke(clip.Path, picture);
            }
        }
    }

    private ClipInfo? NextPicture()
    {
        // An Empty Line Ends A Painter, The Last Says So
        bool done;
        lock (_picturesGate)
        {
            if (_waitingPictures.Count > 0 && !_closing.IsCancellationRequested)
            {
                var shown = _shownFirst;
                var index = Math.Max(
                    0,
                    _waitingPictures.FindIndex(clip => shown.Contains(clip.Path))
                );
                var next = _waitingPictures[index];
                _waitingPictures.RemoveAt(index);
                return next;
            }

            done = --_painters == 0;
        }

        if (done)
        {
            PicturesDone?.Invoke();
        }

        return null;
    }

    public Task<ClipInfo?> GetAsync(string path, CancellationToken token)
    {
        return GetAsync(new FileInfo(path), token);
    }

    private async Task<ClipInfo?> GetAsync(
        FileInfo file,
        CancellationToken token,
        Lazy<ConcurrentDictionary<(long Size, DateTime Written), string>>? orphans = null,
        (HashSet<string> Pictures, HashSet<string> Originals)? present = null
    )
    {
        if (!file.Exists)
        {
            return null;
        }

        // A Listing Read Each Folder Once, Else The Disk
        static bool Has(string path, HashSet<string>? names) =>
            names?.Contains(Path.GetFileName(path)) ?? File.Exists(path);

        var path = file.FullName;

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

        var thumbnail = PicturePathOf(file, ThumbnailSuffix);
        var firstSecond = PicturePathOf(file, FirstSecondSuffix);
        var export = ExportPathOf(path, metadata.Export);
        return new ClipInfo(
            path,
            name,
            metadata.RecordedAt,
            metadata.Duration,
            file.Length,
            Has(thumbnail, present?.Pictures) ? thumbnail
                : Has(firstSecond, present?.Pictures) ? firstSecond
                : null,
            metadata.Title,
            metadata.Game,
            !metadata.ForeignOriginal && Has(OriginalPathOf(path), present?.Originals),
            metadata.Width,
            metadata.Height,
            metadata.FrameRate ?? 0,
            metadata.Monitor,
            file.LastWriteTimeUtc,
            metadata.IsEdited,
            File.Exists(export) ? export : null
        );
    }

    private static string? ExportPathOf(string clip, string? export)
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
