using System.Collections.Concurrent;
using System.Globalization;
using LetsClip.Data.Enums;
using NLog;

namespace LetsClip.Services.Media;

public sealed record VideoEncoder(
    string Name,
    string Codec,
    bool IsHardware,
    bool GpuFrames = false
);

public static class VideoEncoders
{
    public static readonly VideoEncoder Amf = new("AMD AMF", "h264_amf", true);
    public static readonly VideoEncoder Nvenc = new("NVIDIA NVENC", "h264_nvenc", true);
    public static readonly VideoEncoder QuickSync = new("Intel Quick Sync", "h264_qsv", true);
    public static readonly VideoEncoder QuickSyncGpu = QuickSync with { GpuFrames = true };
    public static readonly VideoEncoder Software = new("x264 on the CPU", "libx264", false);

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly ConcurrentDictionary<string, bool> s_tested = new();

    public static IReadOnlyList<VideoEncoder> Candidates(IEnumerable<int> adapterVendors)
    {
        // Hardware Of Each Installed Vendor First, The CPU Always Last
        var candidates = new List<VideoEncoder>();
        foreach (var vendor in adapterVendors)
        {
            // Quick Sync Converts On The GPU First, CPU Second
            VideoEncoder[] encoders = vendor switch
            {
                0x1002 => [Amf],
                0x10DE => [Nvenc],
                0x8086 => [QuickSyncGpu, QuickSync],
                _ => [],
            };
            candidates.AddRange(encoders.Where(encoder => !candidates.Contains(encoder)));
        }

        candidates.Add(Software);
        return candidates;
    }

    public static long PeakBytesPerSecond(
        int width,
        int height,
        int frameRate,
        CaptureQuality quality
    )
    {
        return (long)(LoadOf(width, height, frameRate) * TuningOf(quality).Scale * 40_000_000 / 8);
    }

    private static (double Scale, string Level, string AmfLevel) TuningOf(CaptureQuality quality)
    {
        // AMF Counts Quality Up, Matched To x264 By PSNR
        return quality switch
        {
            CaptureQuality.High => (1.75, "19", "48"),
            CaptureQuality.Max => (2.5, "16", "51"),
            _ => (1, "22", "42"),
        };
    }

    private static double LoadOf(int width, int height, int frameRate)
    {
        // Rates Grow Gently Past 1080p60, Small Frames Start Lower
        var frame = width * (double)height / (1920.0 * 1080);
        return Math.Sqrt(Math.Max(Math.Min(1, frame), frame * frameRate / 60));
    }

    private static string Megabits(double megabits)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(megabits):0}M");
    }

    public static IReadOnlyList<string> CaptureArguments(
        VideoEncoder encoder,
        int frameRate,
        int width,
        int height,
        CaptureQuality quality
    )
    {
        // Peaks Stay Capped So The Replay Cache Stays Bounded
        var (scale, level, amfLevel) = TuningOf(quality);
        var load = LoadOf(width, height, frameRate) * scale;
        var target = Megabits(20 * load);
        var peak = Megabits(40 * load);
        // A Keyframe Every Second, So Every Segment Starts Decodable
        var group = frameRate.ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<string> shared = ["-g", group, "-bf", "0", "-profile:v", "high"];
        IReadOnlyList<string> specific = encoder.Codec switch
        {
            "h264_amf" =>
            [
                "-usage",
                "transcoding",
                "-quality",
                "balanced",
                "-rc",
                "qvbr",
                "-qvbr_quality_level",
                amfLevel,
                "-b:v",
                target,
                "-maxrate",
                peak,
                "-bufsize",
                peak,
                "-forced_idr",
                "1",
            ],
            "h264_nvenc" =>
            [
                "-preset",
                "p4",
                "-tune",
                "hq",
                "-rc",
                "vbr",
                "-cq",
                level,
                "-b:v",
                "0",
                "-maxrate",
                peak,
                "-bufsize",
                peak,
                "-forced-idr",
                "1",
            ],
            "h264_qsv" =>
            [
                "-preset",
                "medium",
                "-global_quality",
                level,
                "-look_ahead",
                "0",
                // Forced Keyframes Become IDR, So Window Segments Split
                "-forced_idr",
                "1",
            ],
            _ => ["-preset", "veryfast", "-crf", level, "-maxrate", peak, "-bufsize", peak],
        };
        return ["-c:v", encoder.Codec, .. specific, .. shared];
    }

    public static async Task<bool> WorksAsync(
        VideoEncoder encoder,
        int width,
        int height,
        int frameRate,
        CancellationToken token
    )
    {
        // Encodes A Few Real Frames, A Listed Encoder Proves Nothing
        var key = $"{encoder.Codec} {width}x{height} {frameRate}";
        if (s_tested.ContainsKey(key))
        {
            return true;
        }

        var size = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
        ProcessResult result;
        try
        {
            result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-loglevel",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    $"testsrc2=size={size}:rate={frameRate}",
                    "-frames:v",
                    "12",
                    "-vf",
                    "format=nv12",
                    .. CaptureArguments(encoder, frameRate, width, height, CaptureQuality.Normal),
                    "-f",
                    "null",
                    "-",
                ],
                TimeSpan.FromSeconds(20),
                token
            );
        }
        catch (TimeoutException)
        {
            // A Hung Driver Counts As A Failed Test
            s_logger.Info($"Skipped {encoder.Name}, Timed Out");
            return false;
        }

        // Only A Pass Is Remembered, Failures Get Tested Again
        if (result.Succeeded)
        {
            s_tested[key] = true;
            s_logger.Info($"Tested {encoder.Name} At {key}, It Works");
        }
        else
        {
            s_logger.Info($"Skipped {encoder.Name}, {result.LastError}");
        }

        return result.Succeeded;
    }
}
