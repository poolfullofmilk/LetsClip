using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    // Past 1440p Reading Whole Frames Back Costs Too Much
    private const int MaximumWholeWindowPixels = 2560 * 1440;

    // Rumble Cut, Noise Reduced, Gaps Closed, Quiet Words Raised
    private const string VoiceCleaning =
        "highpass=f=100,afftdn=nr=40:nf=-30:tn=1,asplit[dry][key];[key]highpass=f=200,lowpass=f=3000[band];[dry]adelay=12:all=1[late];[late][band]sidechaingate=threshold=0.01:ratio=6:range=0.01:attack=3:release=250,speechnorm=p=0.5:e=6:t=0.02";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(12);

    private readonly Lock _pinGate = new();
    private int _pins;
    private bool _deleteWhenUnused;

    public CaptureOptions Options { get; private set; } = options;

    public void SetSeconds(int seconds)
    {
        // The Cache Grows Or Shrinks From Its Next Segment On
        Options = Options with
        {
            Seconds = seconds,
        };
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
                Options.GameProcess
            );
            Audio = new SegmentProcess(
                "Audio",
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
        // A Still Window's Segment Never Closes, Read It Open
        var path = Directory.EnumerateFiles(Folder, "seg_*.ts").MaxBy(file => (file.Length, file));
        return path is not null && new FileInfo(path).Length > 0
            ? new Segment(path, VideoEnd, double.MaxValue, 0)
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
                File.Delete(old.Path);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A Segment Still Being Read Goes With The Session Folder
                s_logger.Warn($"Failed To Evict Segment {exception.GetType().Name}");
            }
        }
    }

    private List<string> VideoArguments(long baseMicroseconds)
    {
        var monitor = Options.Monitor;
        var rate = Options.FrameRate.ToString(CultureInfo.InvariantCulture);

        // A Game Window Arrives Upright And Sized, A Monitor Unrotated
        var isWindow = Options.GameWindow != 0;
        var rotation = isWindow ? 0 : monitor.Rotation;
        var rotated = rotation is 90 or 270;

        // The GPU Blurs A Smaller Size, So The CPU Scales
        var readsWhole =
            !Encoder.GpuFrames
            && MonitorCatalog.ClientSizeOf(Options.GameWindow) is { } client
            && client.Width * client.Height <= MaximumWholeWindowPixels;
        var sized = readsWhole
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $":width={width}:height={height}");
        var source = isWindow
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"gfxcapture=hwnd={Options.GameWindow}:max_framerate={rate}:capture_cursor=1{sized}:resize_mode=scale_aspect"
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

        // The Encoder Makes Its Own Device, Sharing Crashed AMD
        string[] spareDevice = isWindow
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
            "-colorspace",
            "bt709",
            "-color_primaries",
            "bt709",
            "-color_trc",
            "bt709",
            "-color_range",
            "tv",
            // Encoders Skip The Colour Description, Written Into The Bitstream
            "-bsf:v",
            "h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1:video_full_range_flag=0",
            .. SegmentArguments("seg", flushPackets: isWindow),
        ];
    }

    public static string VoiceFilters(bool clean, double volume)
    {
        // Cleaned First, The Gate Hears The Microphone Unchanged
        var cleaning = clean ? $"{VoiceCleaning}," : "";

        // A Boost Is Held Under Full Scale
        var limiter = volume > 1 ? ",alimiter=limit=0.95:level=0" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{cleaning}volume={volume}{limiter}");
    }

    private static List<string> AudioArguments(CaptureOptions options)
    {
        // A Filtered Microphone Arrives On Channels Three And Four
        var separate = options.SeparatesMicrophone;
        var voice = VoiceFilters(options.CleanMicrophone, options.MicrophoneVolume);
        IReadOnlyList<string> layout = separate
            ? ["-ac", "4", "-ch_layout", "4.0"]
            : ["-ac", "2", "-ch_layout", "stereo"];

        // The Mix Plays Everywhere, Game And Voice Ride Beside It
        IReadOnlyList<string> cleaning = separate
            ?
            [
                "-filter_complex",
                $"[0:a]asplit[a][b];[a]pan=stereo|c0=c0|c1=c1,asplit[system][game];[b]pan=stereo|c0=c2|c1=c3,{voice},asplit[voice][microphone];[system][voice]amix=inputs=2:normalize=0[mix]",
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
            .. cleaning,
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            // Sample Zero Is Media Time One, Shifted On The Output
            "-output_ts_offset",
            LeadSeconds.ToString(CultureInfo.InvariantCulture),
            .. SegmentArguments("aud"),
        ];
    }

    private static List<string> SegmentArguments(string prefix, bool flushPackets = false)
    {
        // Media Clock Timestamps, Each Finished File Listed On Standard Output
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
            $"{prefix}_%06d.ts",
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
        private readonly TaskCompletionSource _firstSegment = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly string _name;
        private readonly string _folder;
        private readonly Action<SegmentProcess, Segment> _added;
        private readonly Task _readers;
        private long _lastSegmentTimestamp = Stopwatch.GetTimestamp();
        private string? _lastWarning;
        private int _warningRepeats;
        private long _lastFrameTimestamp;

        public SegmentProcess(
            string name,
            IReadOnlyList<string> arguments,
            bool takesInput,
            string folder,
            Action<SegmentProcess, Segment> added
        )
        {
            _name = name;
            _folder = folder;
            _added = added;
            Process = FFmpeg.Start(FFmpeg.FFmpegPath, arguments, takesInput, folder);
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
            if (_exited.Task.Wait(5000) && _readers.Wait(2000))
            {
                Process.Dispose();
            }
        }

        private async Task ReadSegmentsAsync()
        {
            // Each Finished Segment Arrives As name,start,end
            await FFmpeg.ReadLinesAsync(
                Process.StandardOutput,
                line =>
                {
                    var parts = line.Split(',');
                    if (
                        parts.Length != 3
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
                        return;
                    }

                    var path = Path.Combine(_folder, parts[0]);
                    var bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                    _added(this, new Segment(path, start, end, bytes));
                    Interlocked.Exchange(ref _lastSegmentTimestamp, Stopwatch.GetTimestamp());
                    _firstSegment.TrySetResult();
                }
            );
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

                    LastError = CleanError(line);
                    s_logger.Info($"FFmpeg {_name} {line}");
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
