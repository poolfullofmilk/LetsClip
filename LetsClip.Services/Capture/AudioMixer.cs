using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LetsClip.Services.Games;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NLog;

namespace LetsClip.Services.Capture;

public sealed class AudioMixer : IDisposable
{
    // Stereo Float At 48 kHz, The Encoder Reads It Raw
    public const int SampleRate = 48000;
    private const int Channels = 2;

    // Writes Trail Real Time So Late Packets Still Land
    private const int TrailFrames = SampleRate * 300 / 1000;

    // Windows Keeps A Second While A Capture Thread Waits
    private const int BufferMilliseconds = 1000;

    // Capture Threads Run In Windows' Audio Class, Never Starved
    private const string ThreadClass = "Audio";

    // 40 ms Chunks, Fewer Wakeups Keep The CPU Asleep
    private const int ChunkFrames = SampleRate / 25;

    // E_NOTFOUND, Windows Has No Device Of That Kind At All
    private const int NoDevice = unchecked((int)0x80070490);

    // A Voice App Started Or Closed Is Noticed Within This
    private static readonly TimeSpan s_voiceCheckPeriod = TimeSpan.FromSeconds(10);
    private const string VoiceKind = "Voice Chat Audio";
    private const string HelperKind = "Game Helper Audio";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly long _origin;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly int _outputChannels;

    // Replaced Whole, The Writer Reads One List Per Chunk
    private volatile AudioSource[] _sources = [];
    private readonly Lock _sourcesGate = new();
    private readonly Timer? _voiceWatch;

    // The Game And Its Starter, Both Trees Hold It
    private readonly int[] _holdingGame = [];

    // A Voice App That Started The Game Records Both
    private readonly int _game;
    private readonly bool _throughStarter;
    private Thread? _writer;
    private volatile bool _stopping;
    private long _emittedFrames;
    private long _muteUntilFrame = long.MinValue;

    // Halved So It Passes The Full Scale Clamp
    private volatile float _halfMicrophoneVolume;

    public AudioMixer(
        long originTimestamp,
        string? systemId,
        string? microphoneId,
        bool separateMicrophone,
        double microphoneVolume,
        int gameProcess
    )
    {
        // A Cleaned Microphone Gets Two Channels, Its Volume A Fifth
        _origin = originTimestamp;
        _outputChannels = separateMicrophone ? (Channels * 2) + 1 : Channels;
        SetMicrophoneVolume(microphoneVolume);
        if (systemId is not null && gameProcess != 0)
        {
            // Only The Game And Voice Chat, Each With Its Children
            var starter = GameWatcher.StarterOf(gameProcess);
            _game = gameProcess;
            _holdingGame = [gameProcess, starter];
            _throughStarter = starter != 0 && GameWatcher.VoiceProcesses().Contains(starter);
            _sources =
            [
                new AudioSource(
                    this,
                    "Game Audio",
                    "",
                    true,
                    0,
                    _throughStarter ? starter : gameProcess
                ),
            ];
        }
        else if (systemId is not null)
        {
            _sources = [new AudioSource(this, "System Audio", systemId, true, 0)];
        }

        if (microphoneId is not null)
        {
            _sources =
            [
                .. _sources,
                new AudioSource(
                    this,
                    "Microphone",
                    microphoneId,
                    false,
                    separateMicrophone ? Channels : 0
                ),
            ];
        }

        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DefaultDeviceChanged += (_, eventArgs) =>
            Reconnect(source =>
                source.FollowsDefault
                && eventArgs.Flow == source.Flow
                && eventArgs.Role == Role.Console
            );
        _notifications.DeviceStateChanged += (_, eventArgs) =>
            Reconnect(source =>
                eventArgs.NewState == DeviceState.Active && source.Wants(eventArgs.DeviceId)
            );
        _notifications.DeviceAdded += (_, eventArgs) =>
            Reconnect(source => source.Wants(eventArgs.DeviceId));
        foreach (var source in _sources)
        {
            // A Slow Device Joins Later, Capture Starts Without It
            _ = Task.Run(source.Connect);
        }

        if (systemId is not null && gameProcess != 0)
        {
            // Its Tree Holds The Game Once, Apart It Doubled
            if (_throughStarter)
            {
                s_logger.Info(
                    $"Recorded {VoiceKind} Of Process {_holdingGame[1]} With The Game It Started, The Game's Own Helpers Apart"
                );
            }

            _voiceWatch = new Timer(_ => FollowVoiceApps());
            _voiceWatch.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    // Windows 10 2004 And Later Record One Process Tree
    [SupportedOSPlatformGuard("windows10.0.19041")]
    public static bool CanRecordProcesses => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public string? Problem =>
        _sources.Select(source => source.Problem).FirstOrDefault(problem => problem is not null);

    private void FollowVoiceApps()
    {
        // Voice Apps And Game Helpers Get Recorders, Closed Ones Go
        try
        {
            var voices = GameWatcher.VoiceProcesses().Except(_holdingGame).ToList();
            var helpers = _throughStarter ? GameWatcher.ChildrenOf(_game) : [];
            lock (_sourcesGate)
            {
                Follow(VoiceKind, voices);
                Follow(HelperKind, helpers);
            }
        }
        catch (Exception exception)
        {
            // A Throwing Timer Ends LetsClip, The Game Still Records
            s_logger.Debug($"Failed To Look For Voice Apps {exception.GetType().Name}");
        }
        finally
        {
            try
            {
                if (!_stopping)
                {
                    _voiceWatch?.Change(s_voiceCheckPeriod, Timeout.InfiniteTimeSpan);
                }
            }
            catch (ObjectDisposedException)
            {
                // The Session Ended Meanwhile
            }
        }
    }

    private void Follow(string kind, IReadOnlyList<int> running)
    {
        // Swapped Whole, The Writer Reads One List Per Chunk
        foreach (
            var gone in _sources.Where(source =>
                source.Kind == kind && !running.Contains(source.ProcessId)
            )
        )
        {
            _sources = [.. _sources.Where(source => source != gone)];
            gone.Dispose();
        }

        foreach (var process in running)
        {
            if (_stopping || _sources.Any(source => source.ProcessId == process))
            {
                continue;
            }

            var source = new AudioSource(this, kind, "", true, 0, process);
            source.Connect();
            _sources = [.. _sources, source];
        }
    }

    public void Attach(Stream output)
    {
        _writer = new Thread(() => WriteLoop(output))
        {
            IsBackground = true,
            Name = "LetsClip Audio Writer",
            Priority = ThreadPriority.AboveNormal,
        };
        _writer.Start();
    }

    public void SetMicrophoneVolume(double volume)
    {
        // The Encoder Multiplies The Cleaned Microphone By It, No Restart
        _halfMicrophoneVolume = (float)(volume / 2);
    }

    public void MuteSystemAudio(TimeSpan duration)
    {
        // Our Own Save Chime Never Lands In The Next Clip
        Interlocked.Exchange(
            ref _muteUntilFrame,
            CurrentFrame() + (long)(duration.TotalSeconds * SampleRate)
        );
    }

    private long CurrentFrame()
    {
        return (long)(
            (Stopwatch.GetTimestamp() - _origin) * (double)SampleRate / Stopwatch.Frequency
        );
    }

    private long FrameOfPerformanceCounter(long hundredNanoseconds)
    {
        // WASAPI Hands Its QPC Time In 100 ns Units
        var originHundreds = _origin * 10_000_000.0 / Stopwatch.Frequency;
        return (long)((hundredNanoseconds - originHundreds) * SampleRate / 10_000_000);
    }

    private void Reconnect(Func<AudioSource, bool> matches)
    {
        // Notifications Run On The Audio Thread, WASAPI Calls Deadlock There
        var targets = _sources.Where(matches).ToList();
        if (targets.Count == 0 || _stopping)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (var source in targets)
            {
                source.Connect();
            }
        });
    }

    private void WriteLoop(Stream output)
    {
        var mix = new float[ChunkFrames * _outputChannels];
        try
        {
            while (!_stopping)
            {
                if (_emittedFrames + ChunkFrames > CurrentFrame() - TrailFrames)
                {
                    Thread.Sleep(20);
                    continue;
                }

                Array.Clear(mix);
                foreach (var source in _sources)
                {
                    source.Drain(_emittedFrames, mix, _outputChannels);
                }

                if (_outputChannels > Channels * 2)
                {
                    var volume = _halfMicrophoneVolume;
                    for (var index = Channels * 2; index < mix.Length; index += _outputChannels)
                    {
                        mix[index] = volume;
                    }
                }

                for (var index = 0; index < mix.Length; index++)
                {
                    mix[index] = Math.Clamp(mix[index], -1f, 1f);
                }

                output.Write(MemoryMarshal.AsBytes(mix.AsSpan()));
                _emittedFrames += ChunkFrames;
                foreach (var source in _sources)
                {
                    source.ReportLate(closing: false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The Encoder Closed Its Input, The Recorder Reports Why
        }
        catch (Exception exception)
        {
            // A Throwing Thread Ends LetsClip, The Stall Check Restarts
            s_logger.Warn($"Failed To Write Audio {exception.GetType().Name}");
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _voiceWatch?.Dispose();
        _writer?.Join(TimeSpan.FromSeconds(2));
        _notifications.Dispose();
        lock (_sourcesGate)
        {
            foreach (var source in _sources)
            {
                source.Dispose();
            }
        }

        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class AudioSource(
        AudioMixer mixer,
        string kind,
        string deviceId,
        bool loopback,
        int channelOffset,
        int processId = 0
    ) : IDisposable
    {
        // Eight Seconds Of Room While The Encoder Starts Reading
        private const int RingFrames = SampleRate * 8;

        // A Packet Within 20 ms Of The Last Continues It
        private const int ContinuityFrames = SampleRate / 50;

        // Drift Past 2 ms Slews One Frame Per Packet
        private const int DriftFrames = SampleRate / 500;

        // Late Packets Ten Seconds Apart Are Separate Incidents
        private static readonly TimeSpan s_lateQuiet = TimeSpan.FromSeconds(10);

        // An Idle Loopback Sends Nothing, A Handover Waits This Long
        private static readonly TimeSpan s_handover = TimeSpan.FromSeconds(1);

        private readonly float[] _ring = new float[RingFrames * Channels];
        private readonly Lock _gate = new();
        private readonly Lock _connection = new();
        private readonly ManualResetEventSlim _tookOver = new();
        private WasapiRecorder? _recorder;
        private WasapiRecorder? _pending;
        private bool _pendingStopped;
        private string? _deviceId;
        private string? _from;
        private volatile bool _offDefault;
        private long? _expectedFrame;
        private long _drainedFrame;
        private long _slewedFrames;
        private bool _disposed;

        // One Incident Of Late Packets, Logged Once It Ends
        private DateTime? _lateSince;
        private DateTime _lateUntil;
        private long _lateSeen;
        private long _lateFrames;
        private long _lateWorstFrames;
        private int _lateGaps;
        private bool _lastWasLate;

        public string? Problem { get; private set; }

        public string Kind => kind;

        // Zero Records A Device, Else This Process And Its Children
        public int ProcessId => processId;

        // Automatic, Or A Picked Device That Is Gone For Now
        public bool FollowsDefault { get; private set; } = deviceId.Length == 0 && processId == 0;

        public DataFlow Flow => loopback ? DataFlow.Render : DataFlow.Capture;

        public bool Wants(string changedId)
        {
            // The Picked Device Coming Back Takes Over From The Default
            var isPicked = string.Equals(changedId, deviceId, StringComparison.OrdinalIgnoreCase);
            return processId == 0
                && (
                    ((Problem is not null || _offDefault) && (FollowsDefault || isPicked))
                    || (FollowsDefault && isPicked)
                );
        }

        public void Connect()
        {
            lock (_connection)
            {
                if (_disposed)
                {
                    return;
                }

                var starting = Stopwatch.GetTimestamp();
                WasapiRecorder? recorder = null;
                try
                {
                    // The Device Already Recorded Is Kept, A Restart Would Gap
                    var endpoint = Target();
                    if (
                        endpoint is not null
                        && Problem is null
                        && _recorder is not null
                        && endpoint.ID == _deviceId
                    )
                    {
                        _offDefault = false;
                        return;
                    }

                    (recorder, var from) = Build(endpoint);
                    var built = recorder;
                    recorder.DataAvailable += (buffer, flags, _, counter) =>
                        OnData(built, buffer, flags, counter);
                    recorder.RecordingStopped += (_, eventArgs) => OnStopped(built, eventArgs);
                    WasapiRecorder? previous;
                    lock (_gate)
                    {
                        previous = _recorder;
                        (_pending, _pendingStopped) = (recorder, false);
                    }

                    _tookOver.Reset();
                    recorder.StartRecording();
                    var took = Stopwatch.GetElapsedTime(starting).TotalMilliseconds;

                    // The Old Recorder Writes Until The New One's First Sound
                    if (previous is not null)
                    {
                        _tookOver.Wait(s_handover);
                    }

                    lock (_gate)
                    {
                        _pending = null;
                        if (_pendingStopped)
                        {
                            throw new InvalidOperationException("The new recorder stopped");
                        }

                        if (_recorder != recorder)
                        {
                            _recorder = recorder;
                            _expectedFrame = null;
                        }
                    }

                    Release(previous);
                    (_deviceId, _from, _offDefault) = (endpoint?.ID, from, false);
                    Problem = null;
                    s_logger.Info(
                        $"Started {kind} From {from}{(took >= 100 ? $" In {took:0} ms" : "")}"
                    );
                }
                catch (Exception exception)
                    when (exception
                            is COMException
                                or InvalidOperationException
                                or ArgumentException
                                or NotSupportedException
                    )
                {
                    lock (_gate)
                    {
                        _pending = null;
                    }

                    if (recorder is not null && recorder != _recorder)
                    {
                        Release(recorder);
                    }

                    // A Failed Start Keeps The Running Device Recording
                    if (Problem is null && _recorder is not null)
                    {
                        _offDefault = true;
                        s_logger.Warn(
                            $"Failed To Start {kind} {exception.GetType().Name}, Kept {_from}"
                        );
                        return;
                    }

                    // A Missing Device Records Silence, Never Another Device
                    s_logger.Warn($"Failed To Start {kind} {exception.GetType().Name}");
                    Disconnect();

                    // Automatic With No Device Records Silence, A New Default Connects
                    Problem =
                        FollowsDefault && exception.HResult == NoDevice
                            ? null
                            : $"{kind} Unavailable";
                }
            }
        }

        private MMDevice? Target()
        {
            // A Process Tree Has No Device, Else Picked Or Default
            if (processId != 0 && CanRecordProcesses)
            {
                return null;
            }

            var picked = AudioDevices.Find(mixer._enumerator, deviceId);
            FollowsDefault = picked is null;
            return picked ?? mixer._enumerator.GetDefaultAudioEndpoint(Flow, Role.Console);
        }

        private (WasapiRecorder Recorder, string From) Build(MMDevice? endpoint)
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
            if (processId != 0 && CanRecordProcesses)
            {
                // Any Device They Play On, Activation Waited For Here
                var recorder = new WasapiRecorderBuilder()
                    .WithProcessLoopback(
                        (uint)processId,
                        ProcessLoopbackMode.IncludeTargetProcessTree
                    )
                    .WithFormat(format)
                    .WithBufferLength(BufferMilliseconds)
                    .WithMmcssThreadPriority(ThreadClass)
                    .BuildAsync()
                    .GetAwaiter()
                    .GetResult();
                return (recorder, $"Process {processId} And Its Children");
            }

            var builder = new WasapiRecorderBuilder()
                .WithDevice(endpoint!)
                .WithFormat(format)
                .WithBufferLength(BufferMilliseconds)
                .WithMmcssThreadPriority(ThreadClass);
            if (loopback)
            {
                builder = builder.WithLoopbackCapture();
            }

            return (builder.Build(), endpoint!.FriendlyName);
        }

        private void Disconnect()
        {
            WasapiRecorder? recorder;
            lock (_gate)
            {
                recorder = _recorder;
                _recorder = null;
            }

            Release(recorder);
            _deviceId = null;
        }

        private static void Release(WasapiRecorder? recorder)
        {
            try
            {
                recorder?.Dispose();
            }
            catch (COMException)
            {
                // A Vanished Device Can Fail Its Own Teardown
            }
        }

        private void OnStopped(WasapiRecorder from, StoppedEventArgs eventArgs)
        {
            lock (_gate)
            {
                // A New Recorder Dying Before Taking Over Fails The Switch
                if (from == _pending)
                {
                    _pendingStopped = true;
                    _tookOver.Set();
                    return;
                }

                // A Recorder Let Go On Purpose Stops Quietly
                if (from != _recorder)
                {
                    return;
                }
            }

            if (eventArgs.Exception is { } exception && !_disposed)
            {
                // A Picked Device Pulled Out Hands Over To The Default
                s_logger.Warn($"Lost {kind} {exception.GetType().Name}");
                Problem = $"{kind} Disconnected";
                if (!FollowsDefault)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        Connect();
                    });
                }
            }
        }

        private void OnData(
            WasapiRecorder from,
            ReadOnlySpan<byte> buffer,
            AudioClientBufferFlags flags,
            long performanceCounter
        )
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            var frames = samples.Length / Channels;
            if (frames == 0)
            {
                return;
            }

            var silent = flags.HasFlag(AudioClientBufferFlags.Silent);
            // LetsClip's Own Sounds Never Reach A Game's Or Voice Tree
            var mutedUntil =
                loopback && processId == 0
                    ? Interlocked.Read(ref mixer._muteUntilFrame)
                    : long.MinValue;
            lock (_gate)
            {
                // A New Recorder Takes Over At Its First Sound
                if (from == _pending)
                {
                    if (silent || !samples.ContainsAnyExcept(0f))
                    {
                        return;
                    }

                    (_recorder, _pending, _expectedFrame) = (from, null, null);
                    _tookOver.Set();
                }
                else if (from != _recorder)
                {
                    return;
                }

                // Each Packet Lands At Its Clock Time, Gaps Stay Silent
                var trusted =
                    performanceCounter > 0 && !flags.HasFlag(AudioClientBufferFlags.TimestampError);
                var frame = trusted
                    ? mixer.FrameOfPerformanceCounter(performanceCounter)
                    : _expectedFrame ?? mixer.CurrentFrame();
                if (
                    _expectedFrame is { } expected
                    && Math.Abs(frame - expected) <= ContinuityFrames
                    && !flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)
                )
                {
                    // A Repeated Frame Is Inaudible, Jumps Left 20 ms Gaps
                    var step =
                        Math.Abs(frame - expected) > DriftFrames ? Math.Sign(frame - expected) : 0;
                    if (step > 0 && expected - 1 >= _drainedFrame)
                    {
                        var gap = (int)(expected % RingFrames) * Channels;
                        var previous = (int)((expected - 1) % RingFrames) * Channels;
                        _ring[gap] = _ring[previous];
                        _ring[gap + 1] = _ring[previous + 1];
                    }

                    _slewedFrames += step;
                    frame = expected + step;
                }

                _expectedFrame = frame + frames;
                var late = Math.Clamp(_drainedFrame - frame, 0, frames);
                if (late > 0 && frame + frames > 0)
                {
                    NoteLate(late, mixer.CurrentFrame() - frame);
                }
                else
                {
                    _lastWasLate = false;
                }

                for (var index = 0; index < frames; index++)
                {
                    var position = frame + index;
                    if (position < _drainedFrame || position >= _drainedFrame + RingFrames)
                    {
                        continue;
                    }

                    var slot = (int)(position % RingFrames) * Channels;
                    var quiet = silent || position < mutedUntil;
                    _ring[slot] = quiet ? 0 : samples[index * Channels];
                    _ring[slot + 1] = quiet ? 0 : samples[(index * Channels) + 1];
                }
            }
        }

        private void NoteLate(long frames, long behind)
        {
            // Counted On The Audio Thread, Never Logged There
            var now = DateTime.Now;
            _lateSince ??= now;
            _lateUntil = now;
            _lateSeen = Stopwatch.GetTimestamp();
            _lateFrames += frames;
            _lateWorstFrames = Math.Max(_lateWorstFrames, behind);
            _lateGaps += _lastWasLate ? 0 : 1;
            _lastWasLate = true;
        }

        public void ReportLate(bool closing)
        {
            // One Line Per Incident, After Ten Quiet Seconds Or Closing
            string report;
            lock (_gate)
            {
                if (
                    _lateSince is not { } since
                    || (!closing && Stopwatch.GetElapsedTime(_lateSeen) < s_lateQuiet)
                )
                {
                    return;
                }

                report = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Dropped {_lateFrames * 1000 / SampleRate} ms Of Late {kind} In {_lateGaps} {(_lateGaps == 1 ? "Gap" : "Gaps")} From {since:HH:mm:ss} To {_lateUntil:HH:mm:ss}, Up To {_lateWorstFrames * 1000 / SampleRate} ms Late"
                );
                (_lateSince, _lateFrames, _lateWorstFrames, _lateGaps) = (null, 0, 0, 0);
            }

            s_logger.Warn(report);
        }

        public void Drain(long fromFrame, float[] mix, int stride)
        {
            lock (_gate)
            {
                var frames = mix.Length / stride;
                for (var index = 0; index < frames; index++)
                {
                    var slot = (int)((fromFrame + index) % RingFrames) * Channels;
                    var target = (index * stride) + channelOffset;
                    mix[target] += _ring[slot];
                    mix[target + 1] += _ring[slot + 1];
                    _ring[slot] = 0;
                    _ring[slot + 1] = 0;
                }

                _drainedFrame = fromFrame + frames;
            }
        }

        public void Dispose()
        {
            lock (_connection)
            {
                _disposed = true;
                Disconnect();
                _tookOver.Dispose();
            }

            // The Last Incident Before A Stop Is Logged Too
            ReportLate(closing: true);

            // How Far The Device Clock Ran From The System's
            s_logger.Debug($"Slewed {kind} By {_slewedFrames} Frames");
        }
    }
}
