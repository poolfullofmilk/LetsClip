using System.Collections.Concurrent;
using System.Globalization;
using NLog;

namespace LetsClip.Services.Media;

public sealed record VideoEncoder(string Name, string Codec, bool IsHardware);

public static class VideoEncoders
{
    public static readonly VideoEncoder Amf = new("AMD AMF", "h264_amf", true);
    public static readonly VideoEncoder Nvenc = new("NVIDIA NVENC", "h264_nvenc", true);
    public static readonly VideoEncoder QuickSync = new("Intel Quick Sync", "h264_qsv", true);
    public static readonly VideoEncoder Software = new("x264 on the CPU", "libx264", false);

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly ConcurrentDictionary<string, bool> s_tested = new();

    public static IReadOnlyList<VideoEncoder> Candidates(IEnumerable<int> adapterVendors)
    {
        // Hardware Of Each Installed Vendor First, The CPU Always Last
        var candidates = new List<VideoEncoder>();
        foreach (var vendor in adapterVendors)
        {
            var encoder = vendor switch
            {
                0x1002 => Amf,
                0x10DE => Nvenc,
                0x8086 => QuickSync,
                _ => null,
            };
            if (encoder is not null && !candidates.Contains(encoder))
            {
                candidates.Add(encoder);
            }
        }

        candidates.Add(Software);
        return candidates;
    }

    public static long PeakBytesPerSecond(int width, int height, int frameRate)
    {
        return (long)(LoadOf(width, height, frameRate) * 40_000_000 / 8);
    }

    private static double LoadOf(int width, int height, int frameRate)
    {
        // Rates Grow Gently With Pixels Per Second Past 1080p60
        return Math.Sqrt(Math.Max(1, width * (double)height * frameRate / (1920.0 * 1080 * 60)));
    }

    private static string Megabits(double megabits)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(megabits):0}M");
    }

    public static IReadOnlyList<string> CaptureArguments(
        VideoEncoder encoder,
        int frameRate,
        int width,
        int height
    )
    {
        // Peaks Stay Capped So The Replay Cache Stays Bounded
        var load = LoadOf(width, height, frameRate);
        var target = Megabits(20 * load);
        var peak = Megabits(40 * load);
        // A Keyframe Every Second, So Every Segment Starts Decodable
        var group = frameRate.ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<string> shared = ["-g", group, "-bf", "0", "-profile:v", "high"];
        IReadOnlyList<string> specific = encoder.Codec switch
        {
            "h264_amf" =>
            [
                "-c:v",
                "h264_amf",
                "-usage",
                "transcoding",
                "-quality",
                "balanced",
                "-rc",
                "qvbr",
                "-qvbr_quality_level",
                "22",
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
                "-c:v",
                "h264_nvenc",
                "-preset",
                "p4",
                "-tune",
                "hq",
                "-rc",
                "vbr",
                "-cq",
                "22",
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
                "-c:v",
                "h264_qsv",
                "-preset",
                "medium",
                "-global_quality",
                "22",
                "-look_ahead",
                "0",
            ],
            _ =>
            [
                "-c:v",
                "libx264",
                "-preset",
                "veryfast",
                "-crf",
                "22",
                "-maxrate",
                peak,
                "-bufsize",
                peak,
            ],
        };
        return [.. specific, .. shared];
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
        if (s_tested.TryGetValue(key, out var known))
        {
            return known;
        }

        var size = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
        var result = await FFmpeg.RunAsync(
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
                .. CaptureArguments(encoder, frameRate, width, height),
                "-f",
                "null",
                "-",
            ],
            TimeSpan.FromSeconds(20),
            token
        );
        if (!result.Succeeded)
        {
            s_logger.Info($"Skipped {encoder.Name}, {result.LastError}");
        }

        s_tested[key] = result.Succeeded;
        return result.Succeeded;
    }
}
