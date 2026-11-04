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

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly Lock _pinGate = new();
    private int _pins;
    private bool _deleteWhenUnused;

    public CaptureOptions Options { get; } = options;

    // High Frame Rates Need Room For Their Whole Replay
    private long CacheBytes =>
        Math.Max(
            CacheByteFloor,
            (long)(
                (Options.Seconds + KeepMarginSeconds)
                * VideoEncoders.PeakBytesPerSecond(width, height, Options.FrameRate)
                * 1.2
            )
        );

    public VideoEncoder Encoder { get; } = encoder;

    public string Folder { get; } = CacheFolders.NewFolder();

    public long OriginTimestamp { get; private set; }

    public SegmentProcess? Video { get; private set; }

    public SegmentProcess? Audio { get; private set; }

    public AudioMixer? Mixer { get; private set; }

    public volatile bool Stopping;

    public string? Warning { get; set; }

    public bool HasEnded => Video?.HasExited != false || Audio?.HasExited == true;

    public Task Ended => Audio is null ? Video!.Exited : Task.WhenAny(Video!.Exited, Audio.Exited);

    public double NewestEnd =>
        Math.Min(Video?.Segments.NewestEnd ?? 0, Audio?.Segments.NewestEnd ?? double.MaxValue);

    public double OldestStart => Video?.Segments.OldestStart ?? double.MaxValue;

    public double MediaNow()
    {
        return Stopwatch.GetElapsedTime(OriginTimestamp).TotalSeconds + LeadSeconds;
    }

    public async Task LaunchAsync(TimeSpan timeout)
    {
        // The First Audio Sample Defines The Media Clock
        OriginTimestamp = Stopwatch.GetTimestamp();
        var originWall = DateTime.UtcNow;
        var baseMicroseconds =
            ((originWall - DateTime.UnixEpoch).Ticks / 10) - (long)(LeadSeconds * 1_000_000);
        if (Options.SystemAudio is not null || Options.Microphone is not null)
        {
            Mixer = new AudioMixer(OriginTimestamp, Options.SystemAudio, Options.Microphone);
            Audio = new SegmentProcess("Audio", AudioArguments(), true, Folder, OnSegmentAdded);
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
        var ready = Task.WhenAll(Video.FirstSegment, Audio?.FirstSegment ?? Task.CompletedTask);
        var finished = await Task.WhenAny(ready, Ended, Task.Delay(timeout));
        if (finished != ready)
        {
            throw new CaptureException(
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

    public IReadOnlyList<Segment> VideoCovering(double from, double to)
    {
        return Video?.Segments.Covering(from, to) ?? [];
    }

    public IReadOnlyList<Segment> AudioCovering(double from, double to)
    {
        return Audio?.Segments.Covering(from, to) ?? [];
    }

    public IDisposable Pin(double from)
    {
        lock (_pinGate)
        {
            _pins++;
        }

        return new PinRelease(this, [Video?.Segments.Pin(from), Audio?.Segments.Pin(from)]);
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
        var rotated = monitor.Rotation is 90 or 270;

        // One Wall Clock Reading Puts Video On The Media Clock
        var clock = string.Create(
            CultureInfo.InvariantCulture,
            $"if(eq(N,0),st(0,(RTCTIME-{baseMicroseconds})/1000000/TB-PTS));PTS+ld(0)"
        );
        var chain = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"ddagrab=output_idx={monitor.OutputIndex}:framerate={rate}:draw_mouse=1:dup_frames=1"
            ),
            $"setpts='{clock}'",
            "hwdownload",
            "format=bgra",
            string.Create(
                CultureInfo.InvariantCulture,
                $"scale={(rotated ? height : width)}:{(rotated ? width : height)}:flags=bilinear:out_color_matrix=bt709:out_range=tv"
            ),
            "format=nv12",
        };
        chain.AddRange(
            monitor.Rotation switch
            {
                90 => ["transpose=clock"],
                270 => ["transpose=cclock"],
                180 => ["hflip", "vflip"],
                _ => [],
            }
        );

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
            "-filter_hw_device",
            "capture",
            "-filter_complex",
            string.Join(",", chain) + "[video]",
            "-map",
            "[video]",
            .. VideoEncoders.CaptureArguments(Encoder, Options.FrameRate, width, height),
            "-fps_mode",
            "passthrough",
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
            .. SegmentArguments("seg"),
        ];
    }

    private static List<string> AudioArguments()
    {
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
            "-ac",
            AudioMixer.Channels.ToString(CultureInfo.InvariantCulture),
            "-ch_layout",
            "stereo",
            "-itsoffset",
            LeadSeconds.ToString(CultureInfo.InvariantCulture),
            "-i",
            "pipe:0",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            .. SegmentArguments("aud"),
        ];
    }

    private static List<string> SegmentArguments(string prefix)
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
            "mpegts_copyts=1",
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

    private void Release()
    {
        lock (_pinGate)
        {
            _pins--;
            if (_pins > 0 || !_deleteWhenUnused)
            {
                return;
            }
        }

        CacheFolders.Delete(Folder);
    }

    private sealed class PinRelease(CaptureSession session, IDisposable?[] pins) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            foreach (var pin in pins)
            {
                pin?.Dispose();
            }

            session.Release();
        }
    }

    public sealed class SegmentProcess
    {
        private readonly Queue<string> _errors = new();
        private readonly TaskCompletionSource _firstSegment = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly string _name;
        private readonly string _folder;
        private readonly Action<SegmentProcess, Segment> _added;
        private long _lastSegmentTimestamp = Stopwatch.GetTimestamp();
        private long _lastFrames;
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

            _ = ReadSegmentsAsync();
            _ = ReadErrorsAsync();
            _ = WaitForExitAsync();
        }

        public Process Process { get; }

        public SegmentCache Segments { get; } = new();

        public Task FirstSegment => _firstSegment.Task;

        public Task Exited => _exited.Task;

        public bool HasExited => _exited.Task.IsCompleted;

        public double? FramesPerSecond { get; private set; }

        public int SlowReports { get; private set; }

        public TimeSpan SinceLastSegment =>
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSegmentTimestamp));

        public string? LastError
        {
            get
            {
                lock (_errors)
                {
                    return _errors.LastOrDefault();
                }
            }
        }

        public void Stop()
        {
            FFmpeg.Kill(Process);
            Process.WaitForExit(5000);
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

                    // Warnings Stay Out Of The Reason A Session Ended
                    if (line.Contains("[warning]", StringComparison.Ordinal))
                    {
                        s_logger.Debug($"FFmpeg {_name} {line}");
                        return;
                    }

                    lock (_errors)
                    {
                        _errors.Enqueue(CleanError(line));
                        if (_errors.Count > 20)
                        {
                            _errors.Dequeue();
                        }
                    }

                    s_logger.Info($"FFmpeg {_name} {line}");
                }
            );
        }

        private void CountFrames(long frames)
        {
            // Frames Per Second Of Wall Time Between Two Progress Reports
            var now = Stopwatch.GetTimestamp();
            if (_lastFrameTimestamp != 0 && frames > _lastFrames)
            {
                var seconds = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds;
                FramesPerSecond = (frames - _lastFrames) / Math.Max(seconds, 0.001);
            }

            _lastFrames = frames;
            _lastFrameTimestamp = now;
        }

        public void JudgeFrameRate(int target)
        {
            SlowReports = FramesPerSecond < target * 0.9 ? SlowReports + 1 : 0;
        }

        private async Task WaitForExitAsync()
        {
            await Process.WaitForExitAsync();
            _exited.TrySetResult();
        }

        private static string CleanError(string line)
        {
            // [error] [in#0 @ 0x...] Message Keeps Only The Message
            var cut = line.LastIndexOf("] ", StringComparison.Ordinal);
            return cut >= 0 ? line[(cut + 2)..].Trim() : line.Trim();
        }
    }
}
