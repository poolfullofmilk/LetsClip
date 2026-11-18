using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using LetsClip.Data.Helpers;
using LetsClip.Services.Media;
using NLog;

namespace LetsClip.Services.Capture;

public sealed class CaptureSession(
    CaptureOptions options,
    VideoEncoder encoder,
    int width,
    int height
)
{
    // Media Time Zero Sits Before The Session, Never Negative
    public const double LeadSeconds = 1;
    public const double SegmentSeconds = 1;

    // The Cache Is Bounded By Age And By Size
    private const double KeepMarginSeconds = 3;
    private const long CacheByteFloor = 1_500_000_000;

    // A Gigabyte In Memory, A Sixteenth Of Smaller Machines
    private static readonly long s_memoryBytes = Math.Min(
        1L << 30,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16
    );

    // Loud Game And Voice Together Stay Under Full Scale
    private const string Limiter = ",alimiter=limit=0.95:level=0";

    // Rumble Cut, Noise Reduced, Gaps Closed, Quiet Words Raised
    private const string VoiceCleaning =
        "highpass=f=100,afftdn=nr=40:nf=-30:tn=1,asplit[dry][key];[key]highpass=f=200,lowpass=f=3000[band];[dry]adelay=12:all=1[late];[late][band]sidechaingate=threshold=0.01:ratio=6:range=0.01:attack=3:release=250,speechnorm=p=0.5:e=6:t=0.02";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(12);

    private readonly Lock _pinGate = new();
    private TaskCompletionSource _segmentAdded = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _pins;
    private bool _deleteWhenUnused;
    private bool _spilled;

    public CaptureOptions Options { get; private set; } = options;

    public static bool CanAdjust(CaptureOptions running, CaptureOptions wanted)
    {
        // The Length, Or A Volume The Same Filters Can Carry
        return running with
            {
                Seconds = wanted.Seconds,
                MicrophoneVolume = wanted.MicrophoneVolume,
            } == wanted
            && (
                running.MicrophoneVolume == wanted.MicrophoneVolume
                || (
                    running.SeparatesMicrophone
                    && wanted.SeparatesMicrophone
                    && running.MicrophoneVolume > 1 == wanted.MicrophoneVolume > 1
                )
            );
    }

    public void Adjust(CaptureOptions wanted)
    {
        // Length From The Next Segment, Volume From The Next Chunk
        Options = Options with
        {
            Seconds = wanted.Seconds,
            MicrophoneVolume = wanted.MicrophoneVolume,
        };
        Mixer?.SetMicrophoneVolume(wanted.MicrophoneVolume);
    }

    // High Frame Rates Need Room For Their Whole Replay
    private long CacheBytes =>
        Math.Max(
            CacheByteFloor,
            (long)(
                (Options.Seconds + KeepMarginSeconds)
                * VideoEncoders.PeakBytesPerSecond(
                    width,
                    height,
                    Options.FrameRate,
                    Options.Quality
                )
                * 1.2
            )
        );

    public VideoEncoder Encoder { get; } = encoder;

    public (int Width, int Height) Size { get; } = (width, height);

    public string Folder { get; } = CacheFolders.NewFolder();

    public long OriginTimestamp { get; private set; }

    public SegmentProcess? Video { get; private set; }

    public SegmentProcess? Audio { get; private set; }

    public AudioMixer? Mixer { get; private set; }

    public volatile bool Stopping;

    public bool HasEnded => Video?.HasExited != false || Audio?.HasExited == true;

    public Task Ended => Audio is null ? Video!.Exited : Task.WhenAny(Video!.Exited, Audio.Exited);

    public double VideoEnd => Video?.Segments.NewestEnd ?? 0;

    public double AudioEnd => Audio?.Segments.NewestEnd ?? double.MaxValue;

    public double NewestEnd => Math.Min(VideoEnd, AudioEnd);

    // Completes With The Next Finished Segment, Video Or Audio
    public Task SegmentAdded => Volatile.Read(ref _segmentAdded).Task;

    public double MediaNow()
    {
        return Stopwatch.GetElapsedTime(OriginTimestamp).TotalSeconds + LeadSeconds;
    }

    public async Task LaunchAsync()
    {
        // The First Audio Sample Defines The Media Clock
        OriginTimestamp = Stopwatch.GetTimestamp();
        var originWall = DateTime.UtcNow;
        var baseMicroseconds =
            ((originWall - DateTime.UnixEpoch).Ticks / 10) - (long)(LeadSeconds * 1_000_000);
        if (Options.SystemAudioId is not null || Options.MicrophoneId is not null)
        {
            Mixer = new AudioMixer(
                OriginTimestamp,
                Options.SystemAudioId,
                Options.MicrophoneId,
                Options.SeparatesMicrophone,
                Options.MicrophoneVolume,
                Options.GameProcess
            );
            Audio = new SegmentProcess(
                "Audio",
                "aud",
                AudioArguments(Options),
                true,
                Folder,
                OnSegmentAdded
            );
            Mixer.Attach(Audio.Process.StandardInput.BaseStream);
        }

        // Separate Processes, Late Audio Never Holds Back Live Video
        Video = new SegmentProcess(
            "Video",
            "seg",
            VideoArguments(baseMicroseconds),
            false,
            Folder,
            OnSegmentAdded
        );
        // A Still Window Sends Nothing, Its Sound Proves The Start
        var isWindow = Options.GameWindow != 0;
        var ready =
            isWindow && Audio is not null
                ? Audio.FirstSegment
                : Task.WhenAll(Video.FirstSegment, Audio?.FirstSegment ?? Task.CompletedTask);
        using var waited = new CancellationTokenSource();
        var finished = await Task.WhenAny(ready, Ended, Task.Delay(s_startTimeout, waited.Token));
        waited.Cancel();
        if (finished != ready && !(isWindow && !HasEnded))
        {
            // Which Half Went Quiet, The Log Answers Reports With
            s_logger.Warn(
                $"Capture Start Waited, Video {Video.FramesSoFar} Frames {Video.FirstSegment.IsCompleted}, Audio {Audio?.FirstSegment.IsCompleted}"
            );
            throw new InvalidOperationException(
                Video.LastError ?? Audio?.LastError ?? "The capture did not start in time"
            );
        }
    }

    public void Shutdown()
    {
        // Audio Devices And Encoders Really Stop Before Stopped Shows
        Stopping = true;
        Mixer?.Dispose();
        Video?.Stop();
        Audio?.Stop();
        DeleteFolderWhenUnused();
    }

    public Segment? OpenVideoSegment()
    {
        // A Still Window's Segment Never Closes, Its Bytes So Far
        return Video?.OpenBytes() is { } bytes
            ? new Segment(Path.Combine(Folder, "seg_open.ts"), VideoEnd, double.MaxValue, bytes)
            : null;
    }

    public void Pin(double from)
    {
        lock (_pinGate)
        {
            _pins++;
        }

        Video?.Segments.Pin(from);
        Audio?.Segments.Pin(from);
    }

    public void Unpin(double from)
    {
        // The Last Save Out Deletes A Stopped Session's Folder
        Video?.Segments.Unpin(from);
        Audio?.Segments.Unpin(from);
        lock (_pinGate)
        {
            if (--_pins > 0 || !_deleteWhenUnused)
            {
                return;
            }
        }

        CacheFolders.Delete(Folder);
    }

    private void OnSegmentAdded(SegmentProcess process, Segment segment)
    {
        var evicted = process.Segments.Add(
            segment,
            Options.Seconds + KeepMarginSeconds,
            CacheBytes
        );
        foreach (var old in evicted)
        {
            try
            {
                old.Release();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A Segment Still Being Read Goes With The Session Folder
                s_logger.Warn($"Failed To Evict Segment {exception.GetType().Name}");
            }
        }

        // Past The Memory Budget The Oldest Wait On Disk
        foreach (var spill in process.Segments.OverMemory(s_memoryBytes))
        {
            try
            {
                spill.Spill();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // Kept In Memory, A Full Disk Stops The Session Soon
                s_logger.Warn($"Failed To Move A Segment To Disk {exception.GetType().Name}");
                continue;
            }

            if (!_spilled)
            {
                _spilled = true;
                s_logger.Info(
                    $"Moved The Oldest Segments To Disk, Past {Formatter.FormatSize(s_memoryBytes)} In Memory"
                );
            }
        }

        // A Waiting Save Wakes Now, Not On Its Next Poll
        Interlocked
            .Exchange(
                ref _segmentAdded,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            )
            .TrySetResult();
    }

    private List<string> VideoArguments(long baseMicroseconds)
    {
        var monitor = Options.Monitor;
        var rate = Options.FrameRate.ToString(CultureInfo.InvariantCulture);

        // A Game Window Arrives Upright And Sized, A Monitor Unrotated
        var isWindow = Options.GameWindow != 0;
        var rotation = isWindow ? 0 : monitor.Rotation;
        var rotated = rotation is 90 or 270;

        // LetsClip's Own Lanczos Sizes The Window On The GPU
        var source = isWindow
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"gfxcapture=hwnd={Options.GameWindow}:max_framerate={rate}:capture_cursor=1:width={width}:height={height}:resize_mode=scale_aspect:scale_mode=lanczos"
            )
            : string.Create(
                CultureInfo.InvariantCulture,
                $"ddagrab=output_idx={monitor.OutputIndex}:framerate={rate}:draw_mouse=1:dup_frames=1"
            );

        // One Wall Clock Reading Puts Video On The Media Clock
        var start = string.Create(
            CultureInfo.InvariantCulture,
            $"if(eq(N,0),st(0,(RTCTIME-{baseMicroseconds})/1000000/TB-PTS);st(1,PTS));"
        );

        // Window Frames Take The Next Free Slot, Monitors Count Frames
        var clock = isWindow
            ? start
                + string.Create(
                    CultureInfo.InvariantCulture,
                    $"st(4,round((PTS+ld(0))*{rate}*TB));st(3,if(gt(ld(4),ld(3)),ld(4),if(eq(ld(4),ld(3)),ld(3)+1,ld(3))));ld(3)/({rate}*TB)"
                )
            : start
                + "st(2,ld(1)+N/(FR*TB));if(gt(abs(PTS-ld(2)),1.5/(FR*TB)),st(1,PTS-N/(FR*TB));st(2,PTS));ld(2)+ld(0)";
        var scaledWidth = rotated ? height : width;
        var scaledHeight = rotated ? width : height;
        var chain = new List<string> { source, $"setpts='{clock}'" };
        if (Encoder.GpuFrames && !Encoder.IsQuickSync)
        {
            // AMF And NVENC Convert The Monitor's Frames On The GPU
            chain.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"scale_d3d11=width={width}:height={height}:format=nv12"
                )
            );
        }
        else if (Encoder.GpuFrames)
        {
            // Quick Sync Turns, Sizes And Converts On The GPU
            var turn = rotation switch
            {
                90 => ":transpose=clock",
                270 => ":transpose=cclock",
                180 => ":transpose=reversal",
                _ => "",
            };
            chain.AddRange([
                "hwmap=derive_device=qsv",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"vpp_qsv=w={scaledWidth}:h={scaledHeight}{turn}:format=nv12:out_range=tv:out_color_matrix=bt709:out_color_primaries=bt709:out_color_transfer=bt709"
                ),
            ]);
        }
        else
        {
            chain.AddRange([
                "hwdownload",
                "format=bgra",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"scale={scaledWidth}:{scaledHeight}:flags=lanczos:out_color_matrix=bt709:out_range=tv"
                ),
                "format=nv12",
            ]);
            chain.AddRange(
                rotation switch
                {
                    90 => ["transpose=clock"],
                    270 => ["transpose=cclock"],
                    180 => ["hflip", "vflip"],
                    _ => [],
                }
            );
        }

        // Off The GPU Route The Encoder Never Shares Its Device
        string[] spareDevice =
            isWindow && Encoder.IsHardware && !Encoder.GpuFrames && !Encoder.IsQuickSync
                ?
                [
                    "-init_hw_device",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"d3d11va=spare:{monitor.AdapterIndex}"
                    ),
                ]
                : [];

        return
        [
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "level+warning",
            "-nostats",
            "-progress",
            "pipe:2",
            "-stats_period",
            "1",
            "-init_hw_device",
            string.Create(CultureInfo.InvariantCulture, $"d3d11va=capture:{monitor.AdapterIndex}"),
            .. spareDevice,
            "-filter_hw_device",
            "capture",
            "-filter_complex",
            string.Join(",", chain) + "[video]",
            "-map",
            "[video]",
            .. VideoEncoders.CaptureArguments(
                Encoder,
                Options.FrameRate,
                width,
                height,
                Options.Quality
            ),
            // A Keyframe Each Second, Game Windows Send Frames Unevenly
            "-force_key_frames",
            "expr:gte(t,n_forced)",
            // A Second Frame In One Slot Drops, Never Doubles Up
            "-fps_mode",
            "vfr",
            .. VideoEncoders.ColourTags,
            // Encoders Skip The Colour Description, Written Into The Bitstream
            "-bsf:v",
            "h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1:video_full_range_flag=0",
            .. SegmentArguments(flushPackets: isWindow),
        ];
    }

    public static string VoiceFilters(bool clean, double volume)
    {
        // Cleaned First, The Gate Hears The Microphone Unchanged
        var cleaning = clean ? $"{VoiceCleaning}," : "";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{cleaning}volume={volume}{LimiterFor(volume)}"
        );
    }

    private static string LimiterFor(double volume)
    {
        // A Boost Is Held Under Full Scale
        return volume > 1 ? Limiter : "";
    }

    private static List<string> AudioArguments(CaptureOptions options)
    {
        // Microphone On Channels Three And Four, Halved Volume On Five
        var separate = options.SeparatesMicrophone;

        // Cleaned In Mono, One Channel Is Half The Work
        var microphone = options.CleanMicrophone
            ? $"pan=mono|c0=0.5*c2+0.5*c3,{VoiceCleaning},pan=stereo|c0=c0|c1=c0"
            : "pan=stereo|c0=c2|c1=c3";
        var limiter = LimiterFor(options.MicrophoneVolume);
        IReadOnlyList<string> layout = separate
            ? ["-ac", "5", "-ch_layout", "5.0"]
            : ["-ac", "2", "-ch_layout", "stereo"];

        // The Mix Plays Everywhere, Game And Voice Ride Beside It
        IReadOnlyList<string> filters = separate
            ?
            [
                "-filter_complex",
                $"[0:a]asplit=3[a][b][c];[a]pan=stereo|c0=c0|c1=c1,asplit[system][game];[b]{microphone}[clean];[c]pan=stereo|c0=2*c4|c1=2*c4[volume];[clean][volume]amultiply{limiter},asplit[voice][microphone];[system][voice]amix=inputs=2:normalize=0{Limiter}[mix]",
                "-map",
                "[mix]",
                "-map",
                "[game]",
                "-map",
                "[microphone]",
            ]
            : [];
        return
        [
            "-hide_banner",
            "-loglevel",
            "level+error",
            "-nostats",
            "-thread_queue_size",
            "1024",
            "-f",
            "f32le",
            "-ar",
            AudioMixer.SampleRate.ToString(CultureInfo.InvariantCulture),
            .. layout,
            "-i",
            "pipe:0",
            .. filters,
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            // Sample Zero Is Media Time One, Shifted On The Output
            "-output_ts_offset",
            LeadSeconds.ToString(CultureInfo.InvariantCulture),
            .. SegmentArguments(),
        ];
    }

    private static List<string> SegmentArguments(bool flushPackets = false)
    {
        // Media Clock Timestamps, Each Finished Segment Listed On Standard Output
        return
        [
            "-copyts",
            "-f",
            "segment",
            "-segment_time",
            SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-segment_format",
            "mpegts",
            "-segment_format_options",
            flushPackets ? "mpegts_copyts=1:flush_packets=1" : "mpegts_copyts=1",
            "-segment_list",
            "pipe:1",
            "-segment_list_type",
            "csv",
            "-reset_timestamps",
            "0",
        ];
    }

    private void DeleteFolderWhenUnused()
    {
        lock (_pinGate)
        {
            _deleteWhenUnused = true;
            if (_pins > 0)
            {
                return;
            }
        }

        CacheFolders.Delete(Folder);
    }

    public sealed class SegmentProcess
    {
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
        private readonly Dictionary<int, TaskCompletionSource<byte[]>> _received = [];
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
        private MemoryStream? _receiving;

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
            catch (Exception exception)
                when (exception is InvalidOperationException or Win32Exception)
            {
                // Normal Priority Still Records, Only With Less Headroom
            }

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

        public void Stop()
        {
            // The Exit Code Is Read And Pipes Drain Before Disposal
            FFmpeg.Kill(Process);
            ClosePipes();
            if (_exited.Task.Wait(5000) && _readers.Wait(2000))
            {
                Process.Dispose();
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

        public byte[]? OpenBytes()
        {
            // What Arrived So Far Of The Segment Being Written
            if (Volatile.Read(ref _receiving) is not { } receiving)
            {
                return null;
            }

            lock (receiving)
            {
                return receiving.Length > 0 ? receiving.ToArray() : null;
            }
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

        private TaskCompletionSource<byte[]> ReceivedOf(int number)
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

        private byte[] Receive(NamedPipeServerStream server)
        {
            try
            {
                server.WaitForConnection();
                lock (_pipeGate)
                {
                    _connecting = -1;
                    if (_stopped)
                    {
                        return [];
                    }
                }

                var receiving = new MemoryStream(Volatile.Read(ref _lastLength) * 5 / 4);
                Volatile.Write(ref _receiving, receiving);
                var chunk = new byte[1 << 16];
                int read;
                while ((read = server.Read(chunk)) > 0)
                {
                    lock (receiving)
                    {
                        receiving.Write(chunk, 0, read);
                    }
                }

                // Closed, So Nothing Here Is Open Any More
                Interlocked.CompareExchange(ref _receiving, null, receiving);
                Volatile.Write(ref _lastLength, Math.Max((int)receiving.Length, 1 << 16));
                return receiving.ToArray();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Stopped Or Broken, An Empty Segment Is Never Kept
                return [];
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

                if (received.Length == 0)
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
}
