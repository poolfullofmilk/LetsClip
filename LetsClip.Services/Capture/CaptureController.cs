using System.Diagnostics;
using LetsClip.Data.Enums;
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

    private readonly Lock _gate = new();

    // Starts And Stops Run One At A Time, Whoever Asks
    private readonly SemaphoreSlim _steps = new(1, 1);
    private CaptureHolds _holds;
    private string? _lastGame;
    private IReadOnlyDictionary<string, AddedGame>? _installedGames;
    private int _failures;
    private int _retry;
    private volatile bool _quietRetry;
    private readonly Lazy<bool> _hasHardwareEncoder = new(() =>
        VideoEncoders.Candidates(MonitorCatalog.AdapterVendors()).Any(encoder => encoder.IsHardware)
    );

    public CaptureController()
    {
        Settings = UserSettings.Load();
        Library = new ClipLibrary(Settings.ClipsDirectory);
        Recorder = new ReplayRecorder(Library);
        Recorder.Failed += ranLong => _ = RetryAsync(ranLong);
        Games.GameChanged += name => _ = OnGameChangedAsync(name);
        RefreshMonitors();
        _ = LoadGamesAsync();
    }

    public event Action? Changed;

    public UserSettings Settings { get; private set; }

    public ReplayRecorder Recorder { get; }

    public ClipLibrary Library { get; }

    public GameWatcher Games { get; } = new();

    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];

    // Nothing Picked Or Unplugged Means The Primary Monitor
    public MonitorInfo? SelectedMonitor =>
        MonitorCatalog.Find(Monitors, Settings.MonitorDevicePath)
        ?? Monitors.FirstOrDefault(monitor => monitor.IsPrimary);

    // Up To The Monitor's Rounded Refresh Rate, 30 Always Offered
    public IReadOnlyList<int> FrameRateChoices =>
        [
            .. UserSettings.FrameRateChoices.Where(rate =>
                rate <= Math.Max(30, SelectedMonitor?.RefreshRate ?? 60)
            ),
        ];

    public int FrameRate => FrameRateChoices.Where(rate => rate <= Settings.FrameRate).Max();

    // Lines Below The Monitor's Short Side, Then Its Own Size
    public IReadOnlyList<int> ResolutionChoices
    {
        get
        {
            var own = SelectedMonitor is { } monitor
                ? Math.Min(monitor.Width, monitor.Height)
                : 1080;
            return [.. UserSettings.ResolutionChoices.Where(lines => lines < own), own];
        }
    }

    public int Resolution => ResolutionChoices.Where(lines => lines <= Settings.Resolution).Max();

    // A Graphics Card Records 1080p60 Lightly, The CPU 720p30
    public int RecommendedResolution =>
        ResolutionChoices
            .Where(lines => lines <= (_hasHardwareEncoder.Value ? 1080 : 720))
            .DefaultIfEmpty(ResolutionChoices[0])
            .Max();

    public const CaptureQuality RecommendedQuality = CaptureQuality.Normal;

    public int RecommendedFrameRate =>
        FrameRateChoices.Where(rate => rate <= (_hasHardwareEncoder.Value ? 60 : 30)).Max();

    // A Session That Dropped After Running Restarts Out Of Sight
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
            StartupRegistration.Apply(updated.StartWithWindows);
        }

        Changed?.Invoke();
        _ = StepAsync(null);
    }

    #region Games
    private async Task LoadGamesAsync()
    {
        // The Library Scan Reads Many Folders, Once, Off The Caller
        _installedGames = await Task.Run(GameScanner.Scan);
        ApplyGames();
        Changed?.Invoke();
    }

    private void ApplyGames()
    {
        if (_installedGames is null)
        {
            return;
        }

        // No Scanning While Locked Or Asleep
        Games.SetGames(KnownGames());
        Games.SetEnabled(Settings.GamesOnly && _holds == CaptureHolds.None);
    }

    private Dictionary<string, string> KnownGames()
    {
        return GameList()
            .Where(game => !game.IsRemoved)
            .ToDictionary(game => game.Key, game => game.Name, StringComparer.OrdinalIgnoreCase);
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

    public void AddGame(AddedGame game)
    {
        UpdateSettings(settings =>
            settings with
            {
                AddedGames =
                [
                    .. (settings.AddedGames ?? []).Where(added => added.Key != game.Key),
                    game,
                ],
                RemovedGames =
                [
                    .. (settings.RemovedGames ?? []).Where(removed => removed != game.Key),
                ],
            }
        );
    }

    public void SetRemoved(string key, bool removed)
    {
        // A Switched Off Game Keeps Its Row
        UpdateSettings(settings =>
            settings with
            {
                RemovedGames =
                [
                    .. (settings.RemovedGames ?? []).Where(entry => entry != key),
                    .. removed ? [key] : Array.Empty<string>(),
                ],
            }
        );
    }
    #endregion

    #region Starts And Stops
    public Task StartAutomaticallyAsync()
    {
        return StepAsync(null);
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

        return await Recorder.SaveAsync();
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
        s_logger.Info($"Holding Capture For {hold}, Now {_holds | hold}");
        return StepAsync(() => _holds |= hold, reason);
    }

    public Task ReleaseAsync(CaptureHolds hold)
    {
        s_logger.Info($"Releasing Capture Hold {hold}, Left {_holds & ~hold}");
        return StepAsync(() => _holds &= ~hold);
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

    private async Task RetryAsync(bool ranLong)
    {
        // Every Failure Starts Over By Itself, Set Once And Forget
        var failures = ranLong ? 1 : _failures + 1;
        _failures = failures;
        var retry = Interlocked.Increment(ref _retry);
        _quietRetry = ranLong;
        Changed?.Invoke();
        var seconds = s_retrySeconds[Math.Min(failures, s_retrySeconds.Length) - 1];
        s_logger.Info(
            $"Retrying In {seconds} s, Failure {failures} In A Row{(ranLong ? " After A Long Run, Out Of Sight" : "")}"
        );
        await Task.Delay(TimeSpan.FromSeconds(seconds));
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
            : $"{(options.GameWindow != 0 ? "A Game Window On " : "")}{options.Monitor.Label}, {options.Seconds} s, {options.Resolution}p, {options.FrameRate} FPS, {options.Quality}, System Audio {options.SystemAudioId switch { null => "Off", "" => "Automatic", _ => "Picked" }}, Microphone {options.MicrophoneId switch { null => "Off", "" => "Automatic", _ => "Picked" }}{(options.CleanMicrophone ? " Cleaned" : "")}";
    }

    private CaptureOptions? WantedOptions()
    {
        // Games Only Records The Game's Window, Wherever It Sits
        var settings = Settings;
        var window = settings.GamesOnly ? Games.GameWindow() : 0;
        var monitor = window != 0 ? MonitorCatalog.MonitorOf(Monitors, window) : null;
        if ((settings.GamesOnly && window == 0) || (monitor ?? SelectedMonitor) is not { } shown)
        {
            return null;
        }

        return new CaptureOptions(
            shown,
            settings.ReplaySeconds,
            FrameRate,
            settings.Resolution,
            settings.Quality,
            settings.SystemAudio ? settings.SystemAudioDeviceId : null,
            settings.Microphone ? settings.MicrophoneDeviceId : null,
            settings.Microphone && settings.NoiseCancellation,
            window,
            window != 0 ? Games.GameName : null
        );
    }
    #endregion

    public void Dispose()
    {
        Games.Dispose();
        Recorder.Dispose();
        Library.Dispose();
        _steps.Dispose();
        GC.SuppressFinalize(this);
    }
}
