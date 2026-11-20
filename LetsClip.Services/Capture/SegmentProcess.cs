using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using LetsClip.Services.Media;
using NLog;

namespace LetsClip.Services.Capture;

public sealed class SegmentProcess
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // Pipes Wait Ahead, FFmpeg Opens The Next One At Once
    private const int WaitingPipes = 8;

    // Seconds Of Video Fit, A Busy Moment Never Blocks FFmpeg
    private const int PipeBuffer = 8 << 20;

    private readonly TaskCompletionSource _firstSegment = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _exited = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Lock _pipeGate = new();
    private readonly Queue<NamedPipeServerStream> _waiting = new();
    private readonly Dictionary<int, TaskCompletionSource<SegmentBuffer?>> _received = [];
    private readonly string _name;
    private readonly string _prefix;
    private readonly string _folder;
    private readonly Action<SegmentProcess, Segment> _added;
    private readonly Task _readers;
    private long _lastSegmentTimestamp = Stopwatch.GetTimestamp();
    private string? _lastWarning;
    private int _warningRepeats;
    private long _lastFrameTimestamp;
    private int _nextPipe;
    private int _lastLength = 1 << 16;
    private bool _stopped;
    private int _connecting = -1;
    private SegmentBuilder? _receiving;

    // One Read Chunk For The Receiving Thread, Reused
    private readonly byte[] _chunk = new byte[1 << 16];

    public SegmentProcess(
        string name,
        string prefix,
        IReadOnlyList<string> arguments,
        bool takesInput,
        string folder,
        Action<SegmentProcess, Segment> added
    )
    {
        _name = name;
        _prefix = prefix;
        _folder = folder;
        _added = added;

        // Each Segment Lands In Memory Through Its Own Pipe
        OpenPipes(WaitingPipes);
        try
        {
            Process = FFmpeg.Start(
                FFmpeg.FFmpegPath,
                [.. arguments, $"//./pipe/{PipeName("%06d")}"],
                takesInput,
                folder
            );
        }
        catch
        {
            ClosePipes();
            throw;
        }

        // Read In Order On Its Own Thread, Never The Pool
        new Thread(ReceiveSegments)
        {
            IsBackground = true,
            Name = $"{name} Segments",
            Priority = ThreadPriority.AboveNormal,
        }.Start();

        try
        {
            // Above Normal Keeps Capture Smooth Under A Busy CPU
            Process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Normal Priority Still Records, Only With Less Headroom
        }

        SegmentMemory.Register(Segments);
        _readers = Task.WhenAll(ReadSegmentsAsync(), ReadErrorsAsync());
        _ = WaitForExitAsync();
    }

    public Process Process { get; }

    public SegmentCache Segments { get; } = new();

    public Task FirstSegment => _firstSegment.Task;

    public Task Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    public int? ExitCode { get; private set; }

    public double? FramesPerSecond { get; private set; }

    public long FramesSoFar { get; private set; }

    public int SlowReports { get; private set; }

    public TimeSpan SinceLastSegment =>
        Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSegmentTimestamp));

    public string? LastError { get; private set; }

    // Windows Refused The Window, Another Encoder Would Not Help
    public bool WindowRefused { get; private set; }

    public void Stop()
    {
        // The Exit Code Is Read And Pipes Drain Before Disposal
        FFmpeg.Kill(Process);
        ClosePipes();
        if (_exited.Task.Wait(5000) && _readers.Wait(2000))
        {
            FFmpeg.Release(Process);
        }

        // Received But Never Listed, Its Memory Goes Now
        lock (_pipeGate)
        {
            foreach (var received in _received.Values.Where(received => received.Task.IsCompleted))
            {
                received.Task.Result?.Dispose();
            }

            _received.Clear();
        }
    }

    private void ClosePipes()
    {
        // Waiting Pipes Close, A Pending Wait Gets A Visitor
        int connecting;
        lock (_pipeGate)
        {
            _stopped = true;
            connecting = _connecting;
            while (_waiting.TryDequeue(out var server))
            {
                server.Dispose();
            }
        }

        if (connecting < 0)
        {
            return;
        }

        try
        {
            using var visitor = new NamedPipeClientStream(
                ".",
                PipeName(connecting.ToString("000000", CultureInfo.InvariantCulture)),
                PipeDirection.Out
            );
            visitor.Connect(1000);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // Already Connected Or Gone, The Read Ends Either Way
        }
    }

    public SegmentBuffer? OpenBytes()
    {
        // What Arrived So Far Of The Segment Being Written
        return Volatile.Read(ref _receiving)?.Copy();
    }

    private string PipeName(string number)
    {
        return $"LetsClip-{Path.GetFileName(_folder)}-{_prefix}_{number}.ts";
    }

    private void OpenPipes(int upTo)
    {
        lock (_pipeGate)
        {
            for (; _nextPipe < upTo && !_stopped; _nextPipe++)
            {
                // Only This User, And Never A Name Someone Took First
                _waiting.Enqueue(
                    new NamedPipeServerStream(
                        PipeName(_nextPipe.ToString("000000", CultureInfo.InvariantCulture)),
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
                        PipeBuffer,
                        0
                    )
                );
            }
        }
    }

    private TaskCompletionSource<SegmentBuffer?> ReceivedOf(int number)
    {
        lock (_pipeGate)
        {
            if (!_received.TryGetValue(number, out var received))
            {
                received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _received[number] = received;
            }

            return received;
        }
    }

    private void ReceiveSegments()
    {
        // Segment After Segment, Blocking Reads On This Thread Alone
        for (var number = 0; ; number++)
        {
            NamedPipeServerStream? server;
            lock (_pipeGate)
            {
                if (_stopped || !_waiting.TryDequeue(out server))
                {
                    return;
                }

                _connecting = number;
            }

            OpenPipes(number + 1 + WaitingPipes);
            using (server)
            {
                ReceivedOf(number).TrySetResult(Receive(server));
            }
        }
    }

    private SegmentBuffer? Receive(NamedPipeServerStream server)
    {
        try
        {
            server.WaitForConnection();
            lock (_pipeGate)
            {
                _connecting = -1;
                if (_stopped)
                {
                    return null;
                }
            }

            using var receiving = new SegmentBuilder(Volatile.Read(ref _lastLength) * 5 / 4);
            Volatile.Write(ref _receiving, receiving);
            try
            {
                int read;
                while ((read = server.Read(_chunk)) > 0)
                {
                    receiving.Append(_chunk.AsSpan(0, read));
                }
            }
            finally
            {
                // Closed, So Nothing Here Is Open Any More
                Interlocked.CompareExchange(ref _receiving, null, receiving);
            }

            Volatile.Write(ref _lastLength, Math.Max(receiving.Length, 1 << 16));
            return receiving.Finish();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Stopped Or Broken, Part Of A Segment Is Never Kept
            return null;
        }
    }

    private async Task ReadSegmentsAsync()
    {
        // Each Finished Segment Arrives As name,start,end
        while (await Process.StandardOutput.ReadLineAsync() is { } line)
        {
            var parts = line.Split(',');
            var digits = parts[0].LastIndexOf('_') + 1;
            if (
                parts.Length != 3
                || !parts[0].EndsWith(".ts", StringComparison.Ordinal)
                || !int.TryParse(
                    parts[0].AsSpan(digits, parts[0].Length - digits - 3),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number
                )
                || !double.TryParse(
                    parts[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var start
                )
                || !double.TryParse(
                    parts[2],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var end
                )
            )
            {
                continue;
            }

            // Listed Once Closed, Its Bytes Are In Or Arriving
            var received = await ReceivedOf(number).Task;
            lock (_pipeGate)
            {
                _received.Remove(number);
            }

            if (received is null)
            {
                continue;
            }

            var path = Path.Combine(
                _folder,
                $"{_prefix}_{number.ToString("000000", CultureInfo.InvariantCulture)}.ts"
            );
            _added(this, new Segment(path, start, end, received));
            Interlocked.Exchange(ref _lastSegmentTimestamp, Stopwatch.GetTimestamp());
            _firstSegment.TrySetResult();
        }
    }

    private async Task ReadErrorsAsync()
    {
        await FFmpeg.ReadLinesAsync(
            Process.StandardError,
            line =>
            {
                if (line.StartsWith("frame=", StringComparison.Ordinal))
                {
                    if (
                        long.TryParse(
                            line[6..],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var frames
                        )
                    )
                    {
                        CountFrames(frames);
                    }

                    return;
                }

                if (!line.StartsWith('[') || line.Contains("[info]", StringComparison.Ordinal))
                {
                    return;
                }

                // Warnings Never End A Session, Repeats Are Counted
                if (line.Contains("[warning]", StringComparison.Ordinal))
                {
                    if (line == _lastWarning && ++_warningRepeats % 100 != 0)
                    {
                        return;
                    }

                    s_logger.Debug(
                        $"FFmpeg {_name} {line}{(line == _lastWarning ? $" ({_warningRepeats} Times)" : "")}"
                    );
                    if (line != _lastWarning)
                    {
                        (_lastWarning, _warningRepeats) = (line, 0);
                    }

                    return;
                }

                // Details, The Session's End Is Logged With Its Reason
                LastError = CleanError(line);
                WindowRefused |= line.Contains(
                    "Failed to setup graphics capture",
                    StringComparison.Ordinal
                );
                s_logger.Debug($"FFmpeg {_name} {line}");
            }
        );
    }

    private void CountFrames(long frames)
    {
        // Frames Per Second Of Wall Time Between Two Progress Reports
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp != 0 && frames > FramesSoFar)
        {
            var seconds = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds;
            FramesPerSecond = (frames - FramesSoFar) / Math.Max(seconds, 0.001);
        }

        FramesSoFar = frames;
        _lastFrameTimestamp = now;
    }

    public void JudgeFrameRate(int target)
    {
        SlowReports = FramesPerSecond < target * 0.9 ? SlowReports + 1 : 0;
    }

    private async Task WaitForExitAsync()
    {
        // Exited Always Completes, Whoever Waits On The Session Never Hangs
        try
        {
            await Process.WaitForExitAsync();
            ExitCode = Process.ExitCode;
        }
        finally
        {
            _exited.TrySetResult();
        }
    }

    private static string CleanError(string line)
    {
        // [error] [in#0 @ 0x...] Message Keeps Only The Message
        var cut = line.LastIndexOf("] ", StringComparison.Ordinal);
        return cut >= 0 ? line[(cut + 2)..].Trim() : line.Trim();
    }
}
