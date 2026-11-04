using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NLog;

namespace LetsClip.Services.Games;

public sealed partial class GameWatcher : IDisposable
{
    // One Foreground Look Every Two Seconds, Nothing When Off
    private static readonly TimeSpan s_interval = TimeSpan.FromSeconds(2);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private const uint QueryLimitedInformation = 0x1000;

    private readonly Timer _timer;
    private volatile bool _enabled;
    private int _checking;
    private bool _failing;
    private Process? _game;
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
        _timer.Change(enabled ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void Check()
    {
        // One Check At A Time, The First Scan Is Slow
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_enabled)
            {
                Forget();
                return;
            }

            // A Seen Game Counts Until It Exits, Alt Tab Included
            if (_game is { HasExited: true })
            {
                Forget();
                GameChanged?.Invoke(null);
            }

            if (_game is null && ForegroundGame() is ({ } process, { } name))
            {
                _game = process;
                GameName = name;
                s_logger.Info($"Found {name} Running");
                GameChanged?.Invoke(name);
            }

            _failing = false;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception or ArgumentException)
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
            Volatile.Write(ref _checking, 0);
            if (_enabled)
            {
                _timer.Change(s_interval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Forget()
    {
        _game?.Dispose();
        _game = null;
        GameName = null;
    }

    private (Process Process, string Name)? ForegroundGame()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return null;
        }

        var executable = ExecutableName(processId);
        return
            executable is not null
            && _games.TryGetValue(GameScanner.NormalizeProcessKey(executable), out var name)
            ? (Process.GetProcessById((int)processId), name)
            : null;
    }

    private static string? ExecutableName(uint processId)
    {
        // Limited Rights Read Any Process Name, Elevated Games Too
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            Span<char> buffer = stackalloc char[1024];
            var length = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref length)
                ? Path.GetFileNameWithoutExtension(buffer[..length].ToString())
                : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        _enabled = false;
        _timer.Dispose();
        _game?.Dispose();
        GC.SuppressFinalize(this);
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(
        uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint processId
    );

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(
        nint process,
        uint flags,
        Span<char> name,
        ref int size
    );
}
