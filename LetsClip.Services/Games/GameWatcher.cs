using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Games;

public sealed partial class GameWatcher : IDisposable
{
    // One Process Look Every Two Seconds, Nothing When Off
    private static readonly TimeSpan s_interval = TimeSpan.FromSeconds(2);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // Windows' Own Windows Never Count As Apps To Add
    private static readonly HashSet<string> s_shellHosts =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "explorer",
        "ApplicationFrameHost",
        "TextInputHost",
        "SystemSettings",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "LockApp",
        "msedgewebview2",
    ];

    // Discord's Own Builds, Any Of Them May Run
    private static readonly string[] s_discordNames =
    [
        "Discord",
        "DiscordPTB",
        "DiscordCanary",
        "DiscordDevelopment",
    ];

    private readonly Timer _timer;

    // The Found Game's Handle, One Thread At A Time
    private readonly Lock _gate = new();
    private volatile bool _enabled;
    private int _checking;
    private bool _failing;
    private Process? _game;

    // A Library Game, Never An Added App
    private bool _isGame;
    private volatile IReadOnlyDictionary<string, GameEntry> _games =
        new Dictionary<string, GameEntry>();

    public GameWatcher()
    {
        _timer = new Timer(_ => Check(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<string?>? GameChanged;

    public string? GameName { get; private set; }

    // Zero While No Game Is Found
    public int GameProcessId
    {
        get
        {
            lock (_gate)
            {
                return _game?.Id ?? 0;
            }
        }
    }

    public static IReadOnlyList<int> DiscordProcesses()
    {
        // Each Build's Oldest Process, Its Tree Plays Every Voice
        var processes = Process.GetProcesses();
        try
        {
            return
            [
                .. processes
                    .Where(process =>
                        s_discordNames.Contains(
                            process.ProcessName,
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                    .GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .Select(build => build.MinBy(StartTimeOf)!.Id),
            ];
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static DateTime StartTimeOf(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Gone Or Hidden, Never Taken For The First
            return DateTime.MaxValue;
        }
    }

    public void SetGames(IReadOnlyDictionary<string, GameEntry> games)
    {
        _games = games;

        // A Game Taken Off The List Stops Being Recorded
        bool removed;
        lock (_gate)
        {
            removed = _game is { } game && !games.ContainsKey(KeyOf(game));
        }

        if (removed)
        {
            ForgetGame();
        }
    }

    private static string KeyOf(Process process)
    {
        try
        {
            return GameScanner.NormalizeProcessKey(process.ProcessName);
        }
        catch (InvalidOperationException)
        {
            // Exited Meanwhile, The Next Check Lets It Go
            return "";
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        ForgetGame();
        _timer.Change(enabled ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static AddedGame GameOf(string executablePath)
    {
        // Process Name Is The Key, Product Name Reads Best
        var file = Path.GetFileNameWithoutExtension(executablePath);
        var product = FileVersionInfo.GetVersionInfo(executablePath).ProductName?.Trim();
        return new AddedGame(
            GameScanner.NormalizeProcessKey(file),
            string.IsNullOrEmpty(product) ? file : product,
            executablePath
        );
    }

    public static IReadOnlyList<AddedGame> FindApps()
    {
        // Start Menu Names First, Open Apps Add Portable Ones
        return
        [
            .. GameScanner
                .ScanStartMenu()
                .Concat(RunningApps())
                .Where(app => !app.Key.StartsWith("letsclip", StringComparison.Ordinal))
                .DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static List<AddedGame> RunningApps()
    {
        // Apps With A Titled Window, Named Like An Added Exe
        var processes = Process.GetProcesses();
        try
        {
            return
            [
                .. processes
                    .Where(process =>
                        process.Id != Environment.ProcessId
                        && process.MainWindowHandle != 0
                        && process.MainWindowTitle.Length > 0
                        && !s_shellHosts.Contains(process.ProcessName)
                    )
                    .Select(AppOf)
                    .DistinctBy(app => app.Key),
            ];
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static AddedGame? ForegroundApp()
    {
        // The App In Front, Offered When A Press Found Nothing
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.Id == Environment.ProcessId || s_shellHosts.Contains(process.ProcessName)
                ? null
                : AppOf(process);
        }
        catch (Exception exception)
            when (exception
                    is ArgumentException
                        or InvalidOperationException
                        or Win32Exception
                        or FileNotFoundException
            )
        {
            // Gone Meanwhile, Nothing To Offer
            return null;
        }
    }

    private static AddedGame AppOf(Process process)
    {
        return PathOf(process) is { } path
            ? GameOf(path)
            : new AddedGame(
                GameScanner.NormalizeProcessKey(process.ProcessName),
                process.ProcessName
            );
    }

    private static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // An Elevated App Hides Its Path, The Process Name Serves
            return null;
        }
    }

    public nint GameWindow()
    {
        lock (_gate)
        {
            // Read Fresh, Games Recreate Their Window On A Mode Change
            try
            {
                _game?.Refresh();
                return _game?.MainWindowHandle ?? 0;
            }
            catch (InvalidOperationException)
            {
                // Gone Meanwhile, No Window To Record
                return 0;
            }
        }
    }

    private void Check()
    {
        // One Check At A Time, The First Scan Is Slow
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        Process[] processes = [];
        try
        {
            if (!_enabled)
            {
                return;
            }

            // A Found Game Counts Until Its Own Process Exits
            var running = GameName;
            if (running is not null && GameClosed())
            {
                s_logger.Info($"Found {running} Closed");
                ForgetGame();
                GameChanged?.Invoke(null);
                running = null;
            }

            // A Game Stays, An App Gives Way To One
            if (running is not null && _isGame)
            {
                return;
            }

            // A New Game Needs A Window On Screen To Record
            processes = Process.GetProcesses();
            var found = processes
                .Select(process =>
                    (
                        Process: process,
                        Game: _games.GetValueOrDefault(
                            GameScanner.NormalizeProcessKey(process.ProcessName)
                        )
                    )
                )
                .Where(candidate =>
                    candidate.Game is { } game
                    && (running is null || game.IsInstalledGame)
                    && candidate.Process.Id != Environment.ProcessId
                    && candidate.Process.MainWindowHandle is not 0 and var window
                    && !IsIconic(window)
                    && IsSameProgram(candidate.Process, game)
                )
                .OrderByDescending(candidate => candidate.Game!.IsInstalledGame)
                .FirstOrDefault();
            if (found.Game is { } next)
            {
                // Turned Off Meanwhile, The Game Waits For The Next Start
                lock (_gate)
                {
                    if (!_enabled)
                    {
                        return;
                    }

                    _game?.Dispose();
                    _game = found.Process;
                    _isGame = next.IsInstalledGame;
                    GameName = next.Name;
                }

                s_logger.Info(
                    running is null
                        ? $"Found {next.Name} Running"
                        : $"Found {next.Name} Running, A Game Goes Before {running}"
                );
                GameChanged?.Invoke(next.Name);
                return;
            }

            _failing = false;
        }
        catch (Exception exception)
        {
            // A Throwing Timer Ends LetsClip, The Next Tick Checks Again
            if (!_failing)
            {
                s_logger.Warn($"Failed To Check For A Game {exception.GetType().Name}");
                _failing = true;
            }
        }
        finally
        {
            foreach (var process in processes.Where(process => process != _game))
            {
                process.Dispose();
            }

            Volatile.Write(ref _checking, 0);
            if (_enabled)
            {
                _timer.Change(s_interval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static bool IsSameProgram(Process process, GameEntry game)
    {
        // Library Games Have Launchers, Only Added Exes Are Compared
        if (game.IsInstalledGame || !File.Exists(game.Path) || PathOf(process) is not { } running)
        {
            return true;
        }

        // Updates Move Exes, Another Product Under That Name Differs
        var expected = FileVersionInfo.GetVersionInfo(game.Path).ProductName?.Trim();
        var actual = FileVersionInfo.GetVersionInfo(running).ProductName?.Trim();
        return string.IsNullOrEmpty(expected)
            || string.IsNullOrEmpty(actual)
            || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private bool GameClosed()
    {
        lock (_gate)
        {
            // Already Let Go Means Turned Off, Not Closed
            if (_game is not { } game)
            {
                return false;
            }

            try
            {
                return game.HasExited;
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or Win32Exception)
            {
                // A Protected Game Hides Its Handle, Its Id Still Shows
                return !IsRunning(game.Id);
            }
        }
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ForgetGame()
    {
        lock (_gate)
        {
            _game?.Dispose();
            _game = null;
            GameName = null;
        }
    }

    public void Dispose()
    {
        _enabled = false;
        _timer.Dispose();
        ForgetGame();
        GC.SuppressFinalize(this);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
