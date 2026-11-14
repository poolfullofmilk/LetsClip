using System.ComponentModel;
using System.Diagnostics;
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
    private const int TrailFrames = SampleRate * 100 / 1000;

    // 40 ms Chunks, Fewer Wakeups Keep The CPU Asleep
    private const int ChunkFrames = SampleRate / 25;

    // E_NOTFOUND, Windows Has No Device Of That Kind At All
    private const int NoDevice = unchecked((int)0x80070490);

    // A Voice App Started Or Closed Is Noticed Within This
    private static readonly TimeSpan s_voiceCheckPeriod = TimeSpan.FromSeconds(10);
    private const string VoiceKind = "Voice Chat Audio";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly long _origin;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly int _outputChannels;

    // Replaced Whole, The Writer Reads One List Per Chunk
    private volatile AudioSource[] _sources = [];
    private readonly Lock _sourcesGate = new();
    private readonly Timer? _voiceWatch;
    private Thread? _writer;
    private volatile bool _stopping;
    private long _emittedFrames;
    private long _muteUntilFrame = long.MinValue;

    public AudioMixer(
        long originTimestamp,
        string? systemId,
        string? microphoneId,
        bool separateMicrophone,
        int gameProcess
    )
    {
        // A Cleaned Microphone Gets Its Own Two Channels
        _origin = originTimestamp;
        _outputChannels = separateMicrophone ? Channels * 2 : Channels;
        if (systemId is not null && gameProcess != 0)
        {
            // Only The Game And Voice Chat, Each With Its Children
            _sources = [new AudioSource(this, "Game Audio", "", true, 0, gameProcess)];
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
            source.Connect();
        }

        if (systemId is not null && gameProcess != 0)
        {
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
        // Each Running Voice App Gets A Recorder, Closed Ones Go
        try
        {
            var running = GameWatcher.VoiceProcesses();
            lock (_sourcesGate)
            {
                foreach (
                    var gone in _sources.Where(source =>
                        source.Kind == VoiceKind && !running.Contains(source.ProcessId)
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

                    var voice = new AudioSource(this, VoiceKind, "", true, 0, process);
                    voice.Connect();
                    _sources = [.. _sources, voice];
                }
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

                for (var index = 0; index < mix.Length; index++)
                {
                    mix[index] = Math.Clamp(mix[index], -1f, 1f);
                }

                output.Write(MemoryMarshal.AsBytes(mix.AsSpan()));
                _emittedFrames += ChunkFrames;
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

        private readonly float[] _ring = new float[RingFrames * Channels];
        private readonly Lock _gate = new();
        private readonly Lock _connection = new();
        private WasapiRecorder? _recorder;
        private long? _expectedFrame;
        private long _drainedFrame;
        private long _slewedFrames;

        private bool _disposed;
        private long _lateFrames;
        private long _lateReported;

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
                    (Problem is not null && (FollowsDefault || isPicked))
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

                Disconnect();
                try
                {
                    var (recorder, from) = Build();
                    recorder.DataAvailable += OnData;
                    recorder.RecordingStopped += OnStopped;

                    lock (_gate)
                    {
                        _expectedFrame = null;
                    }

                    _recorder = recorder;
                    recorder.StartRecording();
                    Problem = null;
                    s_logger.Info($"Started {kind} From {from}");
                }
                catch (Exception exception)
                    when (exception
                            is COMException
                                or InvalidOperationException
                                or ArgumentException
                                or NotSupportedException
                    )
                {
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

        private (WasapiRecorder Recorder, string From) Build()
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
                    .WithBufferLength(100)
                    .BuildAsync()
                    .GetAwaiter()
                    .GetResult();
                return (recorder, $"Process {processId} And Its Children");
            }

            // An Empty Id Or Missing Device Follows The Default
            var picked = AudioDevices.Find(mixer._enumerator, deviceId);
            FollowsDefault = picked is null;
            var endpoint = picked ?? mixer._enumerator.GetDefaultAudioEndpoint(Flow, Role.Console);
            var builder = new WasapiRecorderBuilder()
                .WithDevice(endpoint)
                .WithFormat(format)
                .WithBufferLength(100);
            if (loopback)
            {
                builder = builder.WithLoopbackCapture();
            }

            return (builder.Build(), endpoint.FriendlyName);
        }

        private void Disconnect()
        {
            if (_recorder is not { } recorder)
            {
                return;
            }

            _recorder = null;
            recorder.DataAvailable -= OnData;
            recorder.RecordingStopped -= OnStopped;
            try
            {
                recorder.Dispose();
            }
            catch (COMException)
            {
                // A Vanished Device Can Fail Its Own Teardown
            }
        }

        private void OnStopped(object? sender, StoppedEventArgs eventArgs)
        {
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
            ReadOnlySpan<byte> buffer,
            AudioClientBufferFlags flags,
            long devicePosition,
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
                    ReportLate(late);
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

        private void ReportLate(long frames)
        {
            // Logged Once Per Streak Window, Not Once Per Packet
            _lateFrames += frames;
            if (Stopwatch.GetElapsedTime(_lateReported) < TimeSpan.FromSeconds(10))
            {
                return;
            }

            s_logger.Info($"Dropped {_lateFrames} Late {kind} Frames");
            _lateFrames = 0;
            _lateReported = Stopwatch.GetTimestamp();
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
            }

            // How Far The Device Clock Ran From The System's
            s_logger.Debug($"Slewed {kind} By {_slewedFrames} Frames");
        }
    }
}
