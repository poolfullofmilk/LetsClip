using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LetsClip.Services.Capture;

public sealed partial class AudioMixer
{
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

        public void Drain(long fromFrame, float[] mix, int stride, float gain)
        {
            lock (_gate)
            {
                var frames = mix.Length / stride;
                for (var index = 0; index < frames; index++)
                {
                    var slot = (int)((fromFrame + index) % RingFrames) * Channels;
                    var target = (index * stride) + channelOffset;
                    mix[target] += _ring[slot] * gain;
                    mix[target + 1] += _ring[slot + 1] * gain;
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
