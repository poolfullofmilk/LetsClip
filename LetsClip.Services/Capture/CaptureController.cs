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

    // Nothing Picked Yet Means The Primary Monitor
    public MonitorInfo? SelectedMonitor =>
        Settings.MonitorDevicePath is { } path
            ? Monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.DevicePath, path, StringComparison.OrdinalIgnoreCase)
            )
            : Monitors.FirstOrDefault(monitor => monitor.IsPrimary);

    // Frame Rates Go Up To What The Picked Monitor Shows
    public IReadOnlyList<int> FrameRateChoices =>
        [
            .. UserSettings.FrameRateChoices.Where(rate =>
                rate <= (SelectedMonitor?.RefreshRate ?? 60) + 1
            ),
        ];

    public int FrameRate => FrameRateChoices.Where(rate => rate <= Settings.FrameRate).Max();

    public bool IsBuffering =>
        Recorder.Status.State is CaptureState.Starting or CaptureState.Buffering;

    public bool IsPaused { get; private set; }

    public string IdleText =>
        IsPaused ? "Paused"
        : Settings.GamesOnly ? "Waiting for a Game"
        : "Not Buffering";

    // Buffering Names The Game It Records
    public string BufferingText =>
        Settings.GamesOnly && Games.GameName is { } game ? $"Buffering {game}" : "Buffering";

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
            before = Settings;
            updated = change(Settings);
            Settings = updated;
        }

        updated.Save();
        Library.SetFolder(updated.ClipsDirectory);
        if (before.StartWithWindows != updated.StartWithWindows)
        {
            StartupRegistration.Apply(updated.StartWithWindows);
        }

        ApplyGames();
        Changed?.Invoke();
        _ = ApplyChangeAsync();
    }

    public void SelectMonitor(MonitorInfo monitor)
    {
        UpdateSettings(settings => settings with { MonitorDevicePath = monitor.DevicePath });
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

        Games.SetGames(KnownGames());
        Games.SetEnabled(Settings.GamesOnly);
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
    public Task StartAsync()
    {
        // Resuming Ends A Pause, Automation Decides The Rest
        return StepAsync(() => IsPaused = false);
    }

    public Task StopAsync()
    {
        // A Pause Holds Off Every Automatic Start Until Resumed
        return StepAsync(() => IsPaused = true);
    }

    public Task StartAutomaticallyAsync()
    {
        return StepAsync(null);
    }

    public async Task InterruptAsync(string reason)
    {
        // Windows Took The Monitor Away, Nothing The User Asked For
        await _steps.WaitAsync();
        try
        {
            await Recorder.StopAsync(reason);
        }
        finally
        {
            _steps.Release();
        }
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
        // The Game Closing Ends The Buffer It Started
        return game is null
            ? StepAsync(
                null,
                $"Stopped because {_lastGame ?? "the game"} closed, history was cleared"
            )
            : StepAsync(() => _lastGame = game);
    }

    private Task ApplyChangeAsync()
    {
        return StepAsync(null);
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
            var settings = Settings;
            var wanted =
                settings.HasSeenCaptureNotice && !IsPaused && _holds == CaptureHolds.None
                    ? WantedOptions()
                    : null;

            // Queued Quick Changes Restart Once, Then Find Nothing To Do
            var running = Recorder.Options;
            if (running == wanted && (stopReason is null || wanted is not null))
            {
                return;
            }

            if (running is not null || stopReason is not null)
            {
                await Recorder.StopAsync(wanted is null ? stopReason : null);
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
            settings.FullResolution,
            settings.Quality,
            settings.SystemAudio ? settings.SystemAudioDeviceId : null,
            settings.Microphone ? settings.MicrophoneDeviceId : null,
            window,
            window != 0 ? Games.GameName : null
        );
    }
    #endregion

    public Task<SaveOutcome> SaveAsync()
    {
        return Recorder.SaveAsync();
    }

    public void Dispose()
    {
        Games.Dispose();
        Recorder.Dispose();
        Library.Dispose();
        _steps.Dispose();
        GC.SuppressFinalize(this);
    }
}
