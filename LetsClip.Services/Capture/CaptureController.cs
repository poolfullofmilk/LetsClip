using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Games;
using LetsClip.Services.Integration;
using LetsClip.Services.Media;
using LetsClip.Services.Storage;

namespace LetsClip.Services.Capture;

public sealed class CaptureController : IDisposable
{
    // A Save Waits This Long For A Start To Finish
    private static readonly TimeSpan s_startWait = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();

    // Starts And Stops Run One At A Time, Whoever Asks
    private readonly SemaphoreSlim _steps = new(1, 1);
    private CaptureHolds _holds;
    private string? _lastGame;
    private IReadOnlyDictionary<string, string>? _installedGames;

    public CaptureController()
    {
        Settings = UserSettings.Load();
        Library = new ClipLibrary(Settings.ClipsDirectory);
        Recorder = new ReplayRecorder(Library);
        Recorder.Dropped += () => _ = RestartAfterDropAsync();
        Games.GameChanged += name => _ = OnGameChangedAsync(name);
        RefreshMonitors();
        _ = LoadGamesAsync();
    }

    public event Action? Changed;

    public UserSettings Settings { get; private set; }

    public ReplayRecorder Recorder { get; }

    public ClipLibrary Library { get; }

    public ClipExporter Exporter { get; } = new();

    public GameWatcher Games { get; } = new();

    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];

    // Nothing Picked Or Unplugged Means The Primary Monitor
    public MonitorInfo? SelectedMonitor =>
        Monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.DevicePath,
                Settings.MonitorDevicePath,
                StringComparison.OrdinalIgnoreCase
            )
        ) ?? Monitors.FirstOrDefault(monitor => monitor.IsPrimary);

    // Up To The Monitor's Refresh Rate, 30 Always Offered
    public IReadOnlyList<int> FrameRateChoices =>
        [
            .. UserSettings.FrameRateChoices.Where(rate =>
                rate <= Math.Max(30, (SelectedMonitor?.RefreshRate ?? 60) + 1)
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

    // Starting And A Restart Already Read As Recording
    public bool IsRecording =>
        Recorder.Status.State is CaptureState.Recording or CaptureState.Starting
        || (Recorder.Status.State == CaptureState.Stopping && _holds == CaptureHolds.None);

    // Recording Names The Game It Records
    public string StatusText =>
        Recorder.Status switch
        {
            { IsSaving: true } => "Saving Clip",
            { State: CaptureState.Error } => "Recording Error",
            _ when IsRecording => Settings.GamesOnly && Games.GameName is { } game
                ? $"Recording {game}"
                : "Recording",
            _ when Settings.GamesOnly => "Waiting for a Game",
            _ => "Not Recording",
        };

    public int? GameCount => _installedGames is null ? null : KnownGames().Count;

    public void RefreshMonitors()
    {
        var monitors = MonitorCatalog.Enumerate();
        lock (_gate)
        {
            Monitors = monitors;
        }

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
        _installedGames = await Task.Run(() => GameScanner.InstalledGames);
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
        // Found And Added Games, Those Taken Off The List Last
        var settings = Settings;
        var names = new Dictionary<string, string>(
            _installedGames ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var added in settings.AddedGames ?? [])
        {
            names[added.Key] = added.Name;
        }

        var removed = new HashSet<string>(
            settings.RemovedGames ?? [],
            StringComparer.OrdinalIgnoreCase
        );
        return
        [
            .. names
                .Select(game => new GameEntry(game.Key, game.Value, removed.Contains(game.Key)))
                .OrderBy(game => game.IsRemoved)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase),
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

    public void RemoveGame(string key)
    {
        UpdateSettings(settings =>
            settings with
            {
                RemovedGames =
                [
                    .. (settings.RemovedGames ?? []).Where(removed => removed != key),
                    key,
                ],
            }
        );
    }

    public void RestoreGame(string key)
    {
        UpdateSettings(settings =>
            settings with
            {
                RemovedGames = [.. (settings.RemovedGames ?? []).Where(removed => removed != key)],
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
        var now = Monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.DevicePath,
                running?.DevicePath,
                StringComparison.OrdinalIgnoreCase
            )
        );
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
        return StepAsync(() => _holds |= hold, reason);
    }

    public Task ReleaseAsync(CaptureHolds hold)
    {
        return StepAsync(() => _holds &= ~hold);
    }

    private Task OnGameChangedAsync(string? game)
    {
        // The Game Closing Ends The Recording It Started
        return game is null
            ? StepAsync(
                null,
                $"Stopped because {_lastGame ?? "the game"} closed, history was cleared"
            )
            : StepAsync(() => _lastGame = game);
    }

    private async Task RestartAfterDropAsync()
    {
        // A Dropped Capture Starts Over By Itself
        await Task.Delay(TimeSpan.FromSeconds(3));
        await StepAsync(null);
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

            if (running is not null || stopReason is not null || clearsError)
            {
                // A Running Save Gets Its Last Segments First
                await Recorder.WaitForSavesAsync(TimeSpan.FromSeconds(5));
                await Recorder.StopAsync(stopReason);
            }

            if (wanted is not null && Recorder.Status.CanStart)
            {
                await Recorder.StartAsync(wanted);
            }
        }
        finally
        {
            _steps.Release();
        }

        Changed?.Invoke();
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
