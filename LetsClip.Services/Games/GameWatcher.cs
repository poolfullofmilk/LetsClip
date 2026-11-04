using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NLog;

namespace LetsClip.Services.Games;

public sealed partial class GameWatcher : IDisposable
{
    // One Process Look Every Two Seconds, Nothing When Off
    private static readonly TimeSpan s_interval = TimeSpan.FromSeconds(2);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

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
