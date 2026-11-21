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

    // Presses Closer Than This Are One, Hammering Saves One Clip
    private static readonly TimeSpan s_pressGap = TimeSpan.FromSeconds(2);
    private long _lastPress;

    // Failures In A Row Wait Longer, At Most A Minute
    private static readonly int[] s_retrySeconds = [2, 5, 15, 30, 60];

    // Failures In A Row Kept Out Of Sight
    private const int QuietFailures = 4;

    // Retries Come Within Two Minutes, Later Ones Count Anew
    private static readonly TimeSpan s_failuresForgotten = TimeSpan.FromMinutes(5);

    // Settings Changed In A Burst Restart Capture Once
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(2);

    // A Session Lost To A Display Change Restarts Soon
    private static readonly TimeSpan s_displayGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_displayRetry = TimeSpan.FromSeconds(0.5);
    private long _displaysChanged;

    private readonly Lock _gate = new();
    private readonly Lock _registering = new();

    // Starts And Stops Run One At A Time, Whoever Asks
    private readonly SemaphoreSlim _steps = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    // A Recorded Game's Own Monitor Sets Its Limits
    private MonitorInfo? _gameMonitor;

    // The Window Last Asked For, Skipped When Windows Refuses It
    private nint _wantedWindow;
    private CaptureHolds _holds;
    private string? _lastGame;
    private IReadOnlyDictionary<string, AddedGame>? _installedGames;
    private int _failures;
    private long _lastFailure;
    private int _retry;
    private int _settings;
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
        Games.WindowChanged += () => _ = StepAsync(null);
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
    public IReadOnlyList<int> FrameRateChoices => RatesFor(_gameMonitor ?? SelectedMonitor);

    private static int[] RatesFor(MonitorInfo? monitor)
    {
        return
        [
            .. UserSettings.FrameRateChoices.Where(rate =>
                rate <= Math.Max(30, monitor?.RefreshRate ?? 60)
            ),
        ];
    }

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
    public string SaveText =>
        $"Save Last {Formatter.FormatDuration(Recorder.Options?.Seconds ?? Settings.ReplaySeconds)}";

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
        _ = StepWhenSettledAsync();
    }

    private async Task StepWhenSettledAsync()
    {
        // A Later Change Takes Over The Wait
        var settings = Interlocked.Increment(ref _settings);
        try
        {
            await Task.Delay(s_settle, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            // LetsClip Is Closing, Nothing Starts Again
            return;
        }

        if (settings == Volatile.Read(ref _settings))
        {
            await StepAsync(null);
        }
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
        {
            // A Failed Scan Keeps The Last List, Recording Still Starts
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

    public GameCapture? GameSettingsOf(string? key)
    {
        return Settings.GameSettings?.FirstOrDefault(game =>
            string.Equals(game.Key, key, StringComparison.OrdinalIgnoreCase)
        );
    }

    public void SetGameSettings(GameCapture picked)
    {
        // Picks Back At Default Leave No Entry Behind
        UpdateSettings(settings =>
            settings with
            {
                GameSettings =
                [
                    .. (settings.GameSettings ?? []).Where(game =>
                        !string.Equals(game.Key, picked.Key, StringComparison.OrdinalIgnoreCase)
                    ),
                    .. picked.IsDefault ? [] : new[] { picked },
                ],
            }
        );
    }

    public void SetRecorded(IReadOnlyCollection<AddedGame> games, bool recorded)
    {
        // Listed Ones Switch, Apps Join The List, In One Write
        var listed = GameList()
            .Select(game => game.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = games.Select(game => game.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = _installedGames;
        UpdateSettings(settings =>
        {
            // Apps Are Added Entries No Library Holds, None Before Scanning
            var removed = (settings.RemovedGames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var apps = (settings.AddedGames ?? [])
                .Where(game => installed?.ContainsKey(game.Key) == false)
                .Select(game => game.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool IsOff(string key) => keys.Contains(key) ? !recorded : removed.Contains(key);
            return settings with
            {
                // An App Switched Off Leaves, Old Versions Kept Both
                AddedGames =
                [
                    .. (settings.AddedGames ?? []).Where(game =>
                        !apps.Contains(game.Key) || !IsOff(game.Key)
                    ),
                    .. recorded ? games.Where(game => !listed.Contains(game.Key)) : [],
                ],

                // Only Library Games Are Kept Switched Off
                RemovedGames =
                [
                    .. removed.Where(key => !keys.Contains(key) && !apps.Contains(key)),
                    .. recorded
                        ? []
                        : keys.Where(key => listed.Contains(key) && !apps.Contains(key)),
                ],
            };
        });
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
        // Each Press Holds The Next Off, Ignored Ones Too
        var pressed = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastPress, pressed);
        if (Stopwatch.GetElapsedTime(previous, pressed) < s_pressGap)
        {
            s_logger.Info("Ignored A Press, Pressed Again Within 2 s");
            return new SaveOutcome(null, null);
        }

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

        var outcome = await Recorder.SaveAsync(MonitorNumberOf);
        if (outcome.Error is not null)
        {
            SaveFailed?.Invoke();
        }

        return outcome;
    }

    // Recording, Or An Ended Session's Replay Still Kept
    public bool CanSave => IsRecording || Recorder.HasKeptReplay;

    // Set While A Recording Runs Until Stopped
    public DateTimeOffset? RecordingSince => Recorder.RecordingSince;

    public bool StartRecording()
    {
        return Recorder.StartRecording(MonitorNumberOf(Recorder.Options));
    }

    public async Task<SaveOutcome> StopRecordingAsync()
    {
        var outcome = await Recorder.StopRecordingAsync();
        if (outcome.Error is not null)
        {
            SaveFailed?.Invoke();
        }

        return outcome;
    }

    private int? MonitorNumberOf(CaptureOptions? recorded)
    {
        // A Monitor Clip Is Named By Its Monitor's Place
        var number = recorded is { GameWindow: 0 } options
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
        return number > 0 ? number : null;
    }

    public Task DisplaysChangedAsync()
    {
        // The Running Monitor Unplugged Or Changed Ends Its Session
        Volatile.Write(ref _displaysChanged, Stopwatch.GetTimestamp());
        var running = Recorder.Options?.Monitor;
        RefreshMonitors();
        var now = MonitorCatalog.Find(Monitors, running?.DevicePath);
        var reason =
            running is null || now == running ? null
            : now is null ? $"Stopped because {running.FriendlyName} was disconnected"
            : $"Stopped because the display settings of {running.FriendlyName} changed";

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
            ? StepAsync(null, $"Stopped because {_lastGame ?? "the app"} closed")
            : StepAsync(() => _lastGame = game);
    }

    private async Task RetryAsync(CaptureFailure failure)
    {
        // A Refused Window Is Skipped, The Next One Tried
        if (failure == CaptureFailure.WindowRefused)
        {
            Games.Refuse(_wantedWindow);
        }

        // Every Failure Starts Over By Itself, Set Once And Forget
        var failures =
            failure
                is CaptureFailure.RanLong
                    or CaptureFailure.WindowClosed
                    or CaptureFailure.WindowRefused
            || Stopwatch.GetElapsedTime(_lastFailure) > s_failuresForgotten
                ? 1
                : _failures + 1;
        _failures = failures;
        _lastFailure = Stopwatch.GetTimestamp();
        var retry = Interlocked.Increment(ref _retry);
        _quietRetry = failure != CaptureFailure.Blocked && failures <= QuietFailures;
        Changed?.Invoke();

        // A Display Change Broke It, Nothing To Wait Out
        var wait =
            Stopwatch.GetElapsedTime(Volatile.Read(ref _displaysChanged)) < s_displayGrace
                ? s_displayRetry
                : TimeSpan.FromSeconds(
                    s_retrySeconds[Math.Min(failures, s_retrySeconds.Length) - 1]
                );

        // A Closed Or Refused Window Is No Failure Of LetsClip
        s_logger.Info(
            failure switch
            {
                CaptureFailure.WindowClosed =>
                    $"Starting Again In {wait.TotalSeconds:0.#} s If The Game Still Runs",
                CaptureFailure.WindowRefused =>
                    $"Trying Another Window Of The Game In {wait.TotalSeconds:0.#} s",
                _ =>
                    $"Retrying In {wait.TotalSeconds:0.#} s, Failure {failures} In A Row, {failure}{(_quietRetry ? ", Out Of Sight" : "")}",
            }
        );
        try
        {
            await Task.Delay(wait, _closing.Token);
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

            // Locked, Asleep, Dark Or Closing, No Replay Is Kept
            if (_holds != CaptureHolds.None)
            {
                Recorder.DiscardKeptReplay();
            }

            // Nothing To Do Unless Options Differ Or An Error Clears
            var running = Recorder.Options;
            var clearsError = wanted is null && Recorder.Status.State == CaptureState.Error;
            if (running == wanted && (stopReason is null || wanted is not null) && !clearsError)
            {
                return;
            }

            // Only The Length Or Volume Differs, The History Stays
            if (
                wanted is not null
                && running is not null
                && stopReason is null
                && CaptureSession.CanAdjust(running, wanted)
            )
            {
                Recorder.Adjust(wanted);
                s_logger.Info(
                    running.Seconds != wanted.Seconds
                        ? $"Changed The Replay Length From {running.Seconds} s To {wanted.Seconds} s, Recording Kept"
                    : running.VoiceVolume != wanted.VoiceVolume
                        ? $"Changed The Voice Chat Volume From {running.VoiceVolume * 100:0}% To {wanted.VoiceVolume * 100:0}%, Recording Kept"
                    : $"Changed The Microphone Volume From {running.MicrophoneVolume * 100:0}% To {wanted.MicrophoneVolume * 100:0}%, Recording Kept"
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
                await Recorder.StopAsync(stopReason, keepReplay: _holds == CaptureHolds.None);
            }

            if (wanted is not null && Recorder.Status.CanStart)
            {
                _wantedWindow = wanted.GameWindow;
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
            : $"{(options.GameWindow != 0 ? "A Game Window On " : "")}{options.Monitor.Label}, {options.Seconds} s, {options.Resolution}p, {options.FrameRate} FPS, {options.Quality}, System Audio {(options.GameProcess != 0 ? $"Of The Game And Voice Chat At {options.VoiceVolume * 100:0}%" : options.SystemAudioId switch { null => "Off", "" => "Automatic", _ => "Picked" })}, Microphone {options.MicrophoneId switch { null => "Off", "" => "Automatic", _ => "Picked" }}{(options.CleanMicrophone ? " Cleaned" : "")}{(options.MicrophoneId is null ? "" : $" At {options.MicrophoneVolume * 100:0}%")}";
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

        // Moved Or No Longer Primary, The Same Picture Keeps Recording
        if (
            Recorder.Options?.Monitor is { } running
            && running with
            {
                Left = shown.Left,
                Top = shown.Top,
                IsPrimary = shown.IsPrimary,
            } == shown
        )
        {
            shown = running;
        }

        // The Same Window Keeps Recording Through Its Monitor's Mode Changes
        if (Recorder.Options is { GameWindow: not 0 } recorded && recorded.GameWindow == window)
        {
            shown = recorded.Monitor;
        }

        // Only The Game's And Discord's Sound, Else Everything Windows Plays
        var gameAudio =
            window != 0 && settings.SystemAudio && settings.OnlyGameAndDiscord
                ? Games.GameProcessId
                : 0;

        // A Game's Own Picks Go Before The Settings Page's
        var own = window != 0 ? GameSettingsOf(Games.GameKey) : null;
        return new CaptureOptions(
            shown,
            own?.Seconds ?? settings.ReplaySeconds,
            RatesFor(shown).Where(rate => rate <= (own?.FrameRate ?? settings.FrameRate)).Max(),
            own?.Resolution ?? settings.Resolution,
            own?.Quality ?? settings.Quality,
            settings.SystemAudio ? settings.SystemAudioDeviceId : null,
            settings.Microphone ? settings.MicrophoneDeviceId : null,
            settings.Microphone && settings.NoiseCancellation,
            settings.Microphone ? settings.MicrophoneVolume : 1,
            window,
            window != 0 ? Games.GameName : null,
            gameAudio,
            gameAudio != 0 ? settings.VoiceVolume : 1
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
