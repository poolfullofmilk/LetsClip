using System.Diagnostics;
using System.Globalization;
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
    private bool _discardWhenUnused;

    public CaptureOptions Options { get; private set; } = options;

    public static bool CanAdjust(CaptureOptions running, CaptureOptions wanted)
    {
        // Length, Voice Chat, Or A Volume The Filters Can Carry
        return running with
            {
                Seconds = wanted.Seconds,
                MicrophoneVolume = wanted.MicrophoneVolume,
                VoiceVolume = wanted.VoiceVolume,
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
        // Length From The Next Segment, Volumes From The Next Chunk
        Options = Options with
        {
            Seconds = wanted.Seconds,
            MicrophoneVolume = wanted.MicrophoneVolume,
            VoiceVolume = wanted.VoiceVolume,
        };
        Mixer?.SetMicrophoneVolume(wanted.MicrophoneVolume);
        Mixer?.SetVoiceVolume(wanted.VoiceVolume);
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
                Options.GameProcess,
                Options.VoiceVolume
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

    public void Shutdown(bool keepReplay = false)
    {
        // Audio Devices And Encoders Really Stop Before Stopped Shows
        Stopping = true;
        Mixer?.Dispose();
        Video?.Stop();
        Audio?.Stop();
        if (!keepReplay)
        {
            Discard();
        }
    }

    public void Discard()
    {
        // Memory And Files Go Once No Save Reads Them
        lock (_pinGate)
        {
            _discardWhenUnused = true;
            if (_pins > 0)
            {
                return;
            }
        }

        ReleaseReplay();
    }

    private void ReleaseReplay()
    {
        Video?.Segments.ReleaseAll();
        Audio?.Segments.ReleaseAll();
        CacheFolders.Delete(Folder);
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
        // The Last Save Out Lets Go Of A Discarded Replay
        Video?.Segments.Unpin(from);
        Audio?.Segments.Unpin(from);
        lock (_pinGate)
        {
            if (--_pins > 0 || !_discardWhenUnused)
            {
                return;
            }
        }

        ReleaseReplay();
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

        SegmentMemory.SpillOver();

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
}
