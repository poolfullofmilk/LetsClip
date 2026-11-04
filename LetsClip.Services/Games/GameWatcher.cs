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
    private static readonly HashSet<string> s_shellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ApplicationFrameHost",
        "TextInputHost",
        "SystemSettings",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "LockApp",
        "msedgewebview2",
    };

    private readonly Timer _timer;
    private volatile bool _enabled;
    private int _checking;
    private bool _failing;
    private int _gameProcessId;
    private volatile IReadOnlyDictionary<string, string> _games = new Dictionary<string, string>();

    public GameWatcher()
    {
        _timer = new Timer(_ => Check(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<string?>? GameChanged;

    public string? GameName { get; private set; }

    public void SetGames(IReadOnlyDictionary<string, string> games)
    {
        _games = games;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        GameName = null;
        _gameProcessId = 0;
        _timer.Change(enabled ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static AddedGame GameOf(string executablePath)
    {
        // Process Name Is The Key, Product Name Reads Best
        var file = Path.GetFileNameWithoutExtension(executablePath);
        var product = FileVersionInfo.GetVersionInfo(executablePath).ProductName?.Trim();
        return new AddedGame(
            GameScanner.NormalizeProcessKey(file),
            string.IsNullOrEmpty(product) ? file : product
        );
    }

    public static IReadOnlyList<AddedGame> RunningApps()
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
                    .Select(process =>
                        PathOf(process) is { } path
                            ? GameOf(path)
                            : new AddedGame(
                                GameScanner.NormalizeProcessKey(process.ProcessName),
                                process.ProcessName
                            )
                    )
                    .DistinctBy(app => app.Key)
                    .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase),
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
        // Read Fresh, Games Recreate Their Window On A Mode Change
        try
        {
            using var process = Process.GetProcessById(_gameProcessId);
            return process.MainWindowHandle;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return 0;
        }
    }

    private void Check()
    {
        // One Check At A Time, The First Scan Is Slow
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        var processes = Process.GetProcesses();
        try
        {
            if (!_enabled)
            {
                return;
            }

            // A Found Game Counts Until Its Process Exits
            if (GameName is { } running)
            {
                if (processes.Any(process => process.Id == _gameProcessId))
                {
                    return;
                }

                s_logger.Info($"Found {running} Closed");
                GameName = null;
                _gameProcessId = 0;
                GameChanged?.Invoke(null);
            }

            // A New Game Needs A Window On Screen To Record
            foreach (var process in processes)
            {
                if (
                    process.Id != Environment.ProcessId
                    && _games.TryGetValue(
                        GameScanner.NormalizeProcessKey(process.ProcessName),
                        out var name
                    )
                    && process.MainWindowHandle is not 0 and var window
                    && !IsIconic(window)
                )
                {
                    _gameProcessId = process.Id;
                    GameName = name;
                    s_logger.Info($"Found {name} Running");
                    GameChanged?.Invoke(name);
                    return;
                }
            }

            _failing = false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // A Process Gone Mid Check Waits For The Next Tick
            if (!_failing)
            {
                s_logger.Warn($"Failed To Check For A Game {exception.GetType().Name}");
                _failing = true;
            }
        }
        finally
        {
            foreach (var process in processes)
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

    public void Dispose()
    {
        _enabled = false;
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
}
