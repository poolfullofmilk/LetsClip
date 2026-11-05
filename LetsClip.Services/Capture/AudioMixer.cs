using System.Diagnostics;
using System.Runtime.InteropServices;
using LetsClip.Data.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NLog;

namespace LetsClip.Services.Capture;

public sealed class AudioMixer : IDisposable
{
    // Stereo Float At 48 kHz, The Encoder Reads It Raw
    public const int SampleRate = 48000;
    public const int Channels = 2;

    // Writes Trail Real Time So Late Packets Still Land
    private const int TrailFrames = SampleRate * 100 / 1000;

    // 40 ms Chunks, Fewer Wakeups Keep The CPU Asleep
    private const int ChunkFrames = SampleRate / 25;

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly long _origin;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly List<AudioSource> _sources = [];
    private Thread? _writer;
    private volatile bool _stopping;
    private long _emittedFrames;
    private long _muteUntilFrame = long.MinValue;

    public AudioMixer(long originTimestamp, string? systemId, string? microphoneId)
    {
        _origin = originTimestamp;
        if (systemId is not null)
        {
            _sources.Add(new AudioSource(this, "System Audio", systemId, true));
        }

        if (microphoneId is not null)
        {
            _sources.Add(new AudioSource(this, "Microphone", microphoneId, false));
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
    }

    public string? Problem =>
        _sources.Select(source => source.Problem).FirstOrDefault(problem => problem is not null);

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

    public long CurrentFrame()
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
        var mix = new float[ChunkFrames * Channels];
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
                    source.Drain(_emittedFrames, mix);
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
    }

    public void Dispose()
    {
        _stopping = true;
        _writer?.Join(TimeSpan.FromSeconds(2));
        _notifications.Dispose();
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class AudioSource(AudioMixer mixer, string kind, string deviceId, bool loopback)
        : IDisposable
    {
        // Eight Seconds Of Room While The Encoder Starts Reading
        private const int RingFrames = SampleRate * 8;

        // A Packet Within 20 ms Of The Last Continues It
        private const int ContinuityFrames = SampleRate / 50;

        private readonly float[] _ring = new float[RingFrames * Channels];
        private readonly Lock _gate = new();
        private readonly Lock _connection = new();
        private WasapiRecorder? _recorder;
        private long? _expectedFrame;
        private long _drainedFrame;

        private bool _disposed;
        private long _lateFrames;
        private long _lateReported;

        public string? Problem { get; private set; }

        public bool FollowsDefault => deviceId.Length == 0;

        public DataFlow Flow => loopback ? DataFlow.Render : DataFlow.Capture;

        public bool Wants(string changedId)
        {
            return Problem is not null
                && (
                    FollowsDefault
                    || string.Equals(changedId, deviceId, StringComparison.OrdinalIgnoreCase)
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
                    // An Empty Id Follows The Windows Default Device
                    var endpoint = FollowsDefault
                        ? mixer._enumerator.GetDefaultAudioEndpoint(Flow, Role.Console)
                        : mixer._enumerator.GetDevice(deviceId);
                    var builder = new WasapiRecorderBuilder()
                        .WithDevice(endpoint)
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
                        .WithBufferLength(100);
                    if (loopback)
                    {
                        builder = builder.WithLoopbackCapture();
                    }

                    var recorder = builder.Build();
                    recorder.DataAvailable += OnData;
                    recorder.RecordingStopped += OnStopped;

                    lock (_gate)
                    {
                        _expectedFrame = null;
                    }

                    recorder.StartRecording();
                    _recorder = recorder;
                    Problem = null;
                    s_logger.Info($"Started {kind} From {endpoint.FriendlyName}");
                }
                catch (Exception exception)
                    when (exception
                            is COMException
                                or InvalidOperationException
                                or ArgumentException
                    )
                {
                    // A Missing Device Records Silence, Never Another Device
                    s_logger.Warn($"Failed To Start {kind} {exception.GetType().Name}");
                    Problem = $"{kind} Unavailable";
                }
            }
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
                s_logger.Warn($"Lost {kind} {exception.GetType().Name}");
                Problem = $"{kind} Disconnected";
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
            var mutedUntil = loopback ? Interlocked.Read(ref mixer._muteUntilFrame) : long.MinValue;
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
                    frame = expected;
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

        public void Drain(long fromFrame, float[] mix)
        {
            lock (_gate)
            {
                var frames = mix.Length / Channels;
                for (var index = 0; index < frames; index++)
                {
                    var slot = (int)((fromFrame + index) % RingFrames) * Channels;
                    mix[index * Channels] += _ring[slot];
                    mix[(index * Channels) + 1] += _ring[slot + 1];
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
        }
    }
}
