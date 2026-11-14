using System.ComponentModel;
using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Games;
using LetsClip.Services.Integration;
using LetsClip.Services.Media;
using LetsClip.Services.Storage;
using NLog;

namespace LetsClip.Services.Capture;

public sealed class CaptureController : IDisposable
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // A Save Waits This Long For A Start To Finish
    private static readonly TimeSpan s_startWait = TimeSpan.FromSeconds(15);

    // Failures In A Row Wait Longer, At Most A Minute
    private static readonly int[] s_retrySeconds = [2, 5, 15, 30, 60];

    // Failures In A Row Kept Out Of Sight
    private const int QuietFailures = 4;

    // Retries Come Within Two Minutes, Later Ones Count Anew
    private static readonly TimeSpan s_failuresForgotten = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Lock _registering = new();

    // Starts And Stops Run One At A Time, Whoever Asks
    private readonly SemaphoreSlim _steps = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    // A Recorded Game's Own Monitor Sets Its Limits
    private MonitorInfo? _gameMonitor;
    private CaptureHolds _holds;
    private string? _lastGame;
    private IReadOnlyDictionary<string, AddedGame>? _installedGames;
    private int _failures;
    private long _lastFailure;
    private int _retry;
    private volatile bool _quietRetry;
    private readonly Lazy<bool> _hasHardwareEncoder = new(() =>
        VideoEncoders.Candidates().Any(encoder => encoder.IsHardware)
    );

    public CaptureController()
    {
        Settings = UserSettings.Load();
        Library = new ClipLibrary(Settings.ClipsDirectory);

        // Named Clips From Before Get Their Name On The File
        _ = Task.Run(Library.NameFilesAsync);
        Recorder = new ReplayRecorder(Library);
        Recorder.Failed += failure => _ = RetryAsync(failure);
        Games.GameChanged += name => _ = OnGameChangedAsync(name);
        RefreshMonitors();
        _ = ScanGamesAsync();
    }

    public event Action? Changed;

    public event Action? SaveFailed;

    public UserSettings Settings { get; private set; }

    public ReplayRecorder Recorder { get; }

    public ClipLibrary Library { get; }

    public GameWatcher Games { get; } = new();

    public ExportRunner Exports { get; } = new();

    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];

    // Nothing Picked Or Unplugged Means The Primary Monitor
    public MonitorInfo? SelectedMonitor =>
        MonitorCatalog.Find(Monitors, Settings.MonitorDevicePath)
        ?? Monitors.FirstOrDefault(monitor => monitor.IsPrimary);

    // Up To The Monitor's Rounded Refresh Rate, 30 Always Offered
    public IReadOnlyList<int> FrameRateChoices =>
        [
            .. UserSettings.FrameRateChoices.Where(rate =>
                rate <= Math.Max(30, (_gameMonitor ?? SelectedMonitor)?.RefreshRate ?? 60)
            ),
        ];

    public int FrameRate => FrameRateChoices.Where(rate => rate <= Settings.FrameRate).Max();

    // Lines Below The Monitor's Short Side, Then Its Own Size
    public IReadOnlyList<int> ResolutionChoices
    {
        get
        {
            var own = (_gameMonitor ?? SelectedMonitor) is { } monitor
                ? Math.Min(monitor.Width, monitor.Height)
                : 1080;
            return [.. UserSettings.ResolutionChoices.Where(lines => lines < own), own];
        }
    }

    public int Resolution => ResolutionChoices.Where(lines => lines <= Settings.Resolution).Max();

    // A Graphics Card Records 1080p60 Lightly, The CPU 720p30
    public int RecommendedResolution =>
        ResolutionChoices
            .Where(lines => lines <= (HasHardwareEncoder ? 1080 : 720))
            .DefaultIfEmpty(ResolutionChoices[0])
            .Max();

    public const CaptureQuality RecommendedQuality = CaptureQuality.Normal;

    public bool HasHardwareEncoder => _hasHardwareEncoder.Value;

    public int RecommendedFrameRate =>
        FrameRateChoices.Where(rate => rate <= (HasHardwareEncoder ? 60 : 30)).Max();

    // Only What Lasts Or Needs The Person Is Ever Shown
    public bool ShowsError => Recorder.Status.State == CaptureState.Error && !_quietRetry;

    // Starting And A Restart Already Read As Recording
    public bool IsRecording =>
        Recorder.Status.State is CaptureState.Recording or CaptureState.Starting
        || (Recorder.Status.State == CaptureState.Stopping && _holds == CaptureHolds.None)
        || (Recorder.Status.State == CaptureState.Error && _quietRetry);

    // Recording Names The Game It Records
    public string StatusText =>
        Recorder.Status switch
        {
            { IsSaving: true } => "Saving Clip",
            _ when ShowsError => "Recording Error",
            _ when IsRecording => Settings.GamesOnly && Games.GameName is { } game
                ? $"Recording {game}"
                : "Recording",
            _ when Settings.GamesOnly => "Waiting for a Game or App",
            _ => "Not Recording",
        };

    // Written Like Every Other Length, Save Last 2m
    public string SaveText => $"Save Last {Formatter.FormatDuration(Settings.ReplaySeconds)}";

    public long ReplayBytes(int seconds)
    {
        // A Replay's Cache At The Picked Settings
        var (width, height) = (_gameMonitor ?? SelectedMonitor) is { } monitor
            ? ReplayRecorder.OutputSize(monitor, Settings.Resolution)
            : (1920, 1080);
        return VideoEncoders.AverageBytesPerSecond(width, height, FrameRate, Settings.Quality)
            * seconds;
    }

    public bool GamesScanned => _installedGames is not null;

    private void RefreshMonitors()
    {
        Monitors = MonitorCatalog.Enumerate();
        s_logger.Info(
            $"Found Monitors {string.Join("; ", Monitors.Select(monitor => $"{monitor.Label} At {monitor.RefreshRate} Hz{(monitor.IsPrimary ? ", Primary" : "")}{(monitor.IsHdr ? ", HDR" : "")}{(monitor.Rotation != 0 ? $", Turned {monitor.Rotation}" : "")}, Adapter {monitor.AdapterIndex} Output {monitor.OutputIndex}"))}"
        );
        Changed?.Invoke();
    }

    public void UpdateSettings(Func<UserSettings, UserSettings> change)
    {
        UserSettings before;
        UserSettings updated;
        lock (_gate)
        {
            // Written In Turn, Quick Changes Land On Disk In Order
            before = Settings;
            updated = change(Settings);
            Settings = updated;
            updated.Save();
        }

        foreach (var changed in updated.ChangesFrom(before))
        {
            s_logger.Info($"Changed Setting {changed}");
        }

        Library.SetFolder(updated.ClipsDirectory);
        if (before.StartWithWindows != updated.StartWithWindows)
        {
            RegisterWithWindows();
        }

        Changed?.Invoke();
        _ = StepAsync(null);
    }

    public void RegisterWithWindows(bool isInstall = false)
    {
        // A Shortcut Write Took 0.8 s, Off The Window's Thread
        _ = Task.Run(() =>
        {
            // In Turn, Each Reads The Setting As It Is Now
            lock (_registering)
            {
                StartupRegistration.Apply(Settings.StartWithWindows, isInstall);
            }
        });
    }

    #region Games
    public async Task ScanGamesAsync()
    {
        // At Launch And When The Window Or Dialog Opens
        var scanning = Stopwatch.StartNew();
        try
        {
            var found = await Task.Run(GameScanner.Scan).ConfigureAwait(false);

            // A Game Installed Since Arrives Switched On
            if (
                _installedGames
                    ?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(found.Keys) != true
            )
            {
                s_logger.Info(
                    $"Found {found.Count} Installed Games In {scanning.ElapsedMilliseconds} ms"
                );
            }

            _installedGames = found;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or InvalidOperationException
            )
        {
            // A Failed Scan Keeps The Last List
            s_logger.Warn($"Failed To Scan For Games {exception.GetType().Name}");
            _installedGames ??= new Dictionary<string, AddedGame>();
        }

        if (_closing.IsCancellationRequested)
        {
            return;
        }

        // Applied Inside A Step, Never Racing One
        await StepAsync(null).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private void ApplyGames()
    {
        if (_installedGames is null)
        {
            return;
        }

        // No Scanning While Locked Or Asleep
        Games.SetGames(
            GameList()
                .Where(game => !game.IsRemoved)
                .ToDictionary(game => game.Key, StringComparer.OrdinalIgnoreCase)
        );
        Games.SetEnabled(Settings.GamesOnly && _holds == CaptureHolds.None);
    }

    public IReadOnlyList<GameEntry> GameList()
    {
        // Found And Added Games, Each Marked If Switched Off
        var settings = Settings;
        var games = new Dictionary<string, AddedGame>(
            _installedGames ?? new Dictionary<string, AddedGame>(),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var added in settings.AddedGames ?? [])
        {
            games[added.Key] = added;
        }

        var removed = new HashSet<string>(
            settings.RemovedGames ?? [],
            StringComparer.OrdinalIgnoreCase
        );
        return
        [
            .. games.Values.Select(game => new GameEntry(
                game.Key,
                game.Name,
                game.Path,
                removed.Contains(game.Key),
                _installedGames?.ContainsKey(game.Key) == true
            )),
        ];
    }

    public void SetRecorded(IReadOnlyCollection<AddedGame> games, bool recorded)
    {
        // Listed Ones Switch, Apps Join The List, In One Write
        var listed = GameList()
            .Select(game => game.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = games.Select(game => game.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UpdateSettings(settings =>
            settings with
            {
                AddedGames = recorded
                    ?
                    [
                        .. settings.AddedGames ?? [],
                        .. games.Where(game => !listed.Contains(game.Key)),
                    ]
                    : settings.AddedGames,
                RemovedGames =
                [
                    .. (settings.RemovedGames ?? []).Where(entry => !keys.Contains(entry)),
                    .. recorded
                        ? []
                        : games.Where(game => listed.Contains(game.Key)).Select(game => game.Key),
                ],
            }
        );
    }
    #endregion

    #region Starts And Stops
    public Task StartAutomaticallyAsync()
    {
        // With No Game Yet, The Encoder Test Runs Unwatched
        if (Settings.GamesOnly)
        {
            _ = Task.Run(WarmUpEncoderAsync);
        }

        return StepAsync(null);
    }

    private async Task WarmUpEncoderAsync()
    {
        // First Tests After An Update Took 9 s, Then Cached
        if (SelectedMonitor is not { } monitor || !FFmpeg.IsAvailable)
        {
            return;
        }

        var (width, height) = ReplayRecorder.OutputSize(monitor, Settings.Resolution);
        try
        {
            await VideoEncoders.WorksAsync(
                VideoEncoders.Candidates()[0],
                width,
                height,
                FrameRate,
                _closing.Token
            );
        }
        catch (Exception exception)
            when (exception
                    is OperationCanceledException
                        or Win32Exception
                        or InvalidOperationException
            )
        {
            // The Real Start Tests Again And Says Why
        }
    }

    public async Task<SaveOutcome> SaveAsync()
    {
        // A Press During A Restart Saves What Follows It
        var waited = Stopwatch.StartNew();
        while (
            (
                _steps.CurrentCount == 0
                || Recorder.Status.State is CaptureState.Starting or CaptureState.Stopping
                || (_quietRetry && Recorder.Status.State == CaptureState.Error)
            )
            && waited.Elapsed < s_startWait
        )
        {
            await Task.Delay(100);
        }

        // A Monitor Clip Is Named By Its Monitor's Place
        var number = Recorder.Options is { GameWindow: 0 } options
            ? Monitors
                .ToList()
                .FindIndex(monitor =>
                    string.Equals(
                        monitor.DevicePath,
                        options.Monitor.DevicePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                ) + 1
            : 0;
        var outcome = await Recorder.SaveAsync(number > 0 ? number : null);
        if (outcome.Error is not null)
        {
            SaveFailed?.Invoke();
        }

        return outcome;
    }

    public Task DisplaysChangedAsync()
    {
        // The Running Monitor Unplugged Or Changed Ends Its Session
        var running = Recorder.Options?.Monitor;
        RefreshMonitors();
        var now = MonitorCatalog.Find(Monitors, running?.DevicePath);
        var reason =
            running is null || now == running ? null
            : now is null
                ? $"Stopped because {running.FriendlyName} was disconnected, history was cleared"
            : $"Stopped because the display settings of {running.FriendlyName} changed, history was cleared";

        // The Step Restarts Only What Differs
        return StepAsync(null, reason);
    }

    public Task HoldAsync(CaptureHolds hold, string reason)
    {
        return StepAsync(() => SetHolds(_holds | hold, $"Held Capture For {hold}"), reason);
    }

    public Task ReleaseAsync(CaptureHolds hold)
    {
        return StepAsync(() => SetHolds(_holds & ~hold, $"Released Capture Hold {hold}"));
    }

    private void SetHolds(CaptureHolds holds, string what)
    {
        // Windows Repeats Its Events, Only A Change Is Logged
        if (holds == _holds)
        {
            return;
        }

        _holds = holds;
        s_logger.Info($"{what}, Now Holding {holds}");
    }

    private Task OnGameChangedAsync(string? game)
    {
        // The Game Closing Ends The Recording It Started
        return game is null
            ? StepAsync(
                null,
                $"Stopped because {_lastGame ?? "the app"} closed, history was cleared"
            )
            : StepAsync(() => _lastGame = game);
    }

    private async Task RetryAsync(CaptureFailure failure)
    {
        // Every Failure Starts Over By Itself, Set Once And Forget
        var failures =
            failure is CaptureFailure.RanLong or CaptureFailure.WindowClosed
            || Stopwatch.GetElapsedTime(_lastFailure) > s_failuresForgotten
                ? 1
                : _failures + 1;
        _failures = failures;
        _lastFailure = Stopwatch.GetTimestamp();
        var retry = Interlocked.Increment(ref _retry);
        _quietRetry = failure != CaptureFailure.Blocked && failures <= QuietFailures;
        Changed?.Invoke();
        var seconds = s_retrySeconds[Math.Min(failures, s_retrySeconds.Length) - 1];

        // A Closed Window Is No Failure, Its Game Closed Too
        s_logger.Info(
            failure == CaptureFailure.WindowClosed
                ? $"Starting Again In {seconds} s If The Game Still Runs"
                : $"Retrying In {seconds} s, Failure {failures} In A Row, {failure}{(_quietRetry ? ", Out Of Sight" : "")}"
        );
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), _closing.Token);
        }
        catch (OperationCanceledException)
        {
            // LetsClip Is Closing, Nothing Starts Again
            return;
        }

        if (retry != Volatile.Read(ref _retry))
        {
            return;
        }

        await StepAsync(null);
        if (retry == Volatile.Read(ref _retry))
        {
            _quietRetry = false;
        }
    }

    private async Task StepAsync(Action? change, string? stopReason = null)
    {
        // Each Ask Runs Alone, Then Capture Matches What Is Wanted
        await _steps.WaitAsync();
        try
        {
            if (_closing.IsCancellationRequested)
            {
                return;
            }

            change?.Invoke();
            ApplyGames();
            var wanted = _holds == CaptureHolds.None ? WantedOptions() : null;

            // Nothing To Do Unless Options Differ Or An Error Clears
            var running = Recorder.Options;
            var clearsError = wanted is null && Recorder.Status.State == CaptureState.Error;
            if (running == wanted && (stopReason is null || wanted is not null) && !clearsError)
            {
                return;
            }

            // Only The Length Differs, The Session And Its History Stay
            if (
                wanted is not null
                && running is not null
                && stopReason is null
                && running with { Seconds = wanted.Seconds } == wanted
            )
            {
                Recorder.SetSeconds(wanted.Seconds);
                s_logger.Info(
                    $"Changed The Replay Length From {running.Seconds} s To {wanted.Seconds} s, Recording Kept"
                );
                return;
            }

            s_logger.Debug(
                $"Step Wants {Describe(wanted)}, Runs {Describe(running)}, Holds {_holds}, State {Recorder.Status.State}"
            );
            if (running is not null || stopReason is not null || clearsError)
            {
                // A Running Save Gets Its Last Segments First
                await Recorder.WaitForSavesAsync(TimeSpan.FromSeconds(5));
                await Recorder.StopAsync(stopReason);
            }

            if (wanted is not null && Recorder.Status.CanStart)
            {
                await Recorder.StartAsync(wanted);

                // The Recorder Names The Monitor, This Names The Game
                if (wanted.GameWindow != 0 && Recorder.Status.State == CaptureState.Recording)
                {
                    s_logger.Info($"Recording {Games.GameName ?? "A Game"}");
                }
            }
        }
        finally
        {
            _steps.Release();
        }

        Changed?.Invoke();
    }

    private static string Describe(CaptureOptions? options)
    {
        // One Line A Log Reader Can Compare Between Steps
        return options is null
            ? "Nothing"
            : $"{(options.GameWindow != 0 ? "A Game Window On " : "")}{options.Monitor.Label}, {options.Seconds} s, {options.Resolution}p, {options.FrameRate} FPS, {options.Quality}, System Audio {(options.GameProcess != 0 ? "Of The Game And Voice Chat" : options.SystemAudioId switch { null => "Off", "" => "Automatic", _ => "Picked" })}, Microphone {options.MicrophoneId switch { null => "Off", "" => "Automatic", _ => "Picked" }}{(options.CleanMicrophone ? " Cleaned" : "")}{(options.MicrophoneId is null ? "" : $" At {options.MicrophoneVolume * 100:0}%")}";
    }

    private CaptureOptions? WantedOptions()
    {
        // Games Only Records The Game's Window, Wherever It Sits
        var settings = Settings;
        var window = settings.GamesOnly ? Games.GameWindow() : 0;
        var monitor = window != 0 ? MonitorCatalog.MonitorOf(Monitors, window) : null;
        _gameMonitor = monitor;
        if ((settings.GamesOnly && window == 0) || (monitor ?? SelectedMonitor) is not { } shown)
        {
            return null;
        }

        // Only The Game's And Discord's Sound, Else Everything Windows Plays
        var gameAudio =
            window != 0
            && settings.SystemAudio
            && settings.OnlyGameAndDiscord
            && AudioMixer.CanRecordProcesses
                ? Games.GameProcessId
                : 0;
        return new CaptureOptions(
            shown,
            settings.ReplaySeconds,
            FrameRate,
            settings.Resolution,
            settings.Quality,
            settings.SystemAudio ? settings.SystemAudioDeviceId : null,
            settings.Microphone ? settings.MicrophoneDeviceId : null,
            settings.Microphone && settings.NoiseCancellation,
            settings.Microphone ? settings.MicrophoneVolume : 1,
            window,
            window != 0 ? Games.GameName : null,
            gameAudio
        );
    }
    #endregion

    public void Dispose()
    {
        _closing.Cancel();
        Games.Dispose();
        Recorder.Dispose();
        Library.Dispose();
        GC.SuppressFinalize(this);
    }
}
