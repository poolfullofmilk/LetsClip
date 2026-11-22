using System.ComponentModel;
using System.Globalization;
using LetsClip.Data.Helpers;

namespace LetsClip.Services.Media;

public static partial class ClipExporter
{
    private static string SoundSource(ExportRequest request, int track, bool balanced)
    {
        // One Track, Or Game And Microphone Mixed At Their Levels
        return balanced
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"[0:a:1]volume={request.GameVolume}[game];[0:a:2]volume={request.MicrophoneVolume}[voice];[game][voice]amix=inputs=2:normalize=0"
            )
            : string.Create(CultureInfo.InvariantCulture, $"[0:a:{track}]anull");
    }

    private static string SoundGraph(ExportRequest request, int track, bool balanced, double gain)
    {
        // Raised Sound And A Rebuilt Mix Keep Their Peaks Under
        var level = string.Create(CultureInfo.InvariantCulture, $"volume={gain}dB");
        var limit = gain > 0 || balanced ? $",{PeakLimit}" : "";
        return $"{SoundSource(request, track, balanced)},{level}{limit}[sound]";
    }

    private static async Task<double> LoudnessGainAsync(
        ExportRequest request,
        int track,
        bool balanced,
        IProgress<ExportProgress> progress,
        CancellationToken token
    )
    {
        // EBU R128 Over The Selection, Silence Left Alone
        progress.Report(new ExportProgress("Measuring Sound", 0));
        var range = request.Range;
        double? loudness = null;
        double? peak = null;
        var section = "";
        try
        {
            await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-nostats",
                    "-ss",
                    FFmpeg.Seconds(range.SeekStart),
                    "-i",
                    request.SourcePath,
                    "-t",
                    FFmpeg.Seconds(range.Length),
                    "-filter_complex",
                    $"{SoundSource(request, track, balanced)},ebur128=peak=true:framelog=quiet[sound]",
                    "-map",
                    "[sound]",
                    "-f",
                    "null",
                    "-",
                ],
                FFmpeg.CopyLimit(new FileInfo(request.SourcePath).Length),
                token,
                errorLine: line =>
                {
                    // Sections Like True peak: Hold Lines Like Peak: -6.5 dBFS
                    var trimmed = line.Trim();
                    if (trimmed.EndsWith(':'))
                    {
                        section = trimmed;
                        return;
                    }

                    var colon = trimmed.IndexOf(':');
                    if (
                        colon <= 0
                        || !double.TryParse(
                            trimmed[(colon + 1)..]
                                .Replace("LUFS", "", StringComparison.Ordinal)
                                .Replace("dBFS", "", StringComparison.Ordinal),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var value
                        )
                    )
                    {
                        return;
                    }

                    if (
                        section == "Integrated loudness:"
                        && trimmed.StartsWith("I:", StringComparison.Ordinal)
                    )
                    {
                        loudness = value;
                    }
                    else if (
                        section == "True peak:"
                        && trimmed.StartsWith("Peak:", StringComparison.Ordinal)
                    )
                    {
                        peak = value;
                    }
                }
            );
        }
        catch (Exception exception) when (exception is TimeoutException or Win32Exception)
        {
            // Unmeasured Sound Exports As Recorded, Never A Failed Export
            s_logger.Info($"Kept The Sound As Recorded, {exception.GetType().Name}");
            return 0;
        }

        // Raised Only Until Peaks Would Need 6 dB Of Limiting
        if (loudness is not (> -60 and < 0))
        {
            return 0;
        }

        var headroom = PeakCeiling - (peak ?? 0) + MostLimiting;
        var gain = Math.Min(TargetLoudness - loudness.Value, Math.Min(MostBoost, headroom));
        s_logger.Debug(
            $"Measured {loudness:0.0} LUFS, Peak {peak:0.0} dBFS, Sound Moves {gain:0.0} dB"
        );
        return Math.Round(gain, 1);
    }

    private sealed record EncodeJob(
        ExportRequest Request,
        string Output,
        string WorkFolder,
        int Width,
        int Height,
        double FrameRate,
        int? AudioTrack,
        string Crop,
        double AudioBitsPerSecond,
        bool Video,
        double Gain,
        bool Balanced
    );

    private static async Task EncodeAsync(
        EncodeJob job,
        VideoEncoder encoder,
        double bitrate,
        int attempt,
        IProgress<ExportProgress> progress,
        CancellationToken token
    )
    {
        // Cuts At Any Frame Re-Encode From The Original Clip
        var range = job.Request.Range;
        var rate = job.FrameRate.ToString(CultureInfo.InvariantCulture);
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"{job.Crop}scale={job.Width}:{job.Height}:force_original_aspect_ratio=decrease:flags=lanczos,pad={job.Width}:{job.Height}:-1:-1:color=black,setsar=1,format=yuv420p"
        );

        // A Hair Early, So The Start Frame Itself Is Kept
        List<string> input =
        [
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-y",
            "-progress",
            "pipe:2",
            "-nostats",
            "-ss",
            FFmpeg.Seconds(range.SeekStart),
            "-i",
            job.Request.SourcePath,
            "-t",
            FFmpeg.Seconds(range.Length),
        ];
        List<string> picture =
        [
            "-map",
            "0:v:0",
            "-vf",
            filter,
            "-r",
            rate,
            "-fps_mode",
            "cfr",
            .. VideoArguments(encoder, bitrate, job.FrameRate),
            .. VideoEncoders.ColourTags,
        ];
        List<string> output = [];
        if (job.AudioTrack is { } track)
        {
            // Sound Alone Retries Its Own Bitrate, Else The Budget's
            var sound = job.Video ? job.AudioBitsPerSecond : bitrate;
            IReadOnlyList<string> codec = job.Video
                ? ["-c:a", "aac"]
                : ["-c:a", "libopus", "-vbr", "constrained", "-application", "audio"];
            output.AddRange([
                "-filter_complex",
                SoundGraph(job.Request, track, job.Balanced, job.Gain),
                "-map",
                "[sound]",
                .. codec,
                "-b:a",
                string.Create(CultureInfo.InvariantCulture, $"{(int)(sound / 1000)}k"),
                "-ac",
                sound < SizeBudget.StereoBitsPerSecond ? "1" : "2",
                "-ar",
                "48000",
            ]);

            // Its Track's Name, So Exported Knows What Was Left Out
            if (track > 0)
            {
                output.AddRange(["-metadata:s:a:0", $"handler_name={AudioTracks.Separate[track]}"]);
            }
        }

        // Sound Alone Goes In Ogg, Discord's Own Voice Message Format
        output.AddRange(
            job.Video
                ? ["-movflags", "+faststart", "-f", "mp4", job.Output]
                : ["-f", "ogg", job.Output]
        );
        var label = attempt == 1 ? "" : $"Try {attempt}, ";
        if (!job.Video)
        {
            // AAC Lands Its Bitrate In One Pass
            await RunPassAsync(
                [.. input, .. output],
                range.Length,
                $"{label}Encoding",
                0,
                1,
                progress,
                token
            );
            return;
        }

        if (encoder.IsHardware)
        {
            await RunPassAsync(
                [.. input, .. picture, .. output],
                range.Length,
                $"{label}Encoding on {encoder.Name}",
                0,
                1,
                progress,
                token,
                s_hardwareStart
            );
            return;
        }

        // Two Passes Of x264 Land A Bitrate Accurately
        var passLog = Path.Combine(job.WorkFolder, "export-pass");
        await RunPassAsync(
            [
                .. input,
                .. picture,
                "-passlogfile",
                passLog,
                "-pass",
                "1",
                "-an",
                "-f",
                "null",
                "NUL",
            ],
            range.Length,
            $"{label}Analysing",
            0,
            0.5,
            progress,
            token
        );
        await RunPassAsync(
            [.. input, .. picture, "-passlogfile", passLog, "-pass", "2", .. output],
            range.Length,
            $"{label}Encoding",
            0.5,
            0.5,
            progress,
            token
        );
    }

    private static List<string> VideoArguments(
        VideoEncoder encoder,
        double bitrate,
        double frameRate
    )
    {
        string Kilobits(double share) =>
            ((int)Math.Max(1, bitrate * share / 1000)).ToString(CultureInfo.InvariantCulture) + "k";

        // Peaks Stay Near The Average, So One Pass Lands Close
        var group = (2 * frameRate).ToString(CultureInfo.InvariantCulture);
        List<string> rate =
        [
            "-b:v",
            Kilobits(1),
            "-maxrate",
            Kilobits(1.5),
            "-bufsize",
            Kilobits(2),
            "-g",
            group,
        ];
        string[] specific = encoder.Codec switch
        {
            "h264_amf" or "hevc_amf" =>
            [
                "-usage",
                "transcoding",
                "-quality",
                "quality",
                "-rc",
                "vbr_peak",
            ],
            "h264_nvenc" or "hevc_nvenc" =>
            [
                "-preset",
                "p6",
                "-tune",
                "hq",
                "-rc",
                "vbr",
                "-multipass",
                "fullres",
                "-spatial-aq",
                "1",
            ],
            "h264_qsv" or "hevc_qsv" => ["-preset", "slower"],
            _ => ["-preset", "medium"],
        };

        // A Card's H.264 Takes Three B Frames, Its HEVC None
        string[] frames = encoder.IsHardware && !encoder.IsHevc ? ["-bf", "3"] : [];

        // Tagged hvc1, So Apple Devices Play It Too
        string[] profile = encoder.IsHevc
            ? ["-profile:v", "main", "-tag:v", "hvc1"]
            : ["-profile:v", "high"];
        return ["-c:v", encoder.Codec, .. specific, .. frames, .. profile, .. rate];
    }

    private static async Task RunPassAsync(
        IReadOnlyList<string> arguments,
        double duration,
        string phase,
        double offset,
        double share,
        IProgress<ExportProgress> progress,
        CancellationToken token,
        TimeSpan? firstProgress = null
    )
    {
        // Progress Arrives As out_time_us Lines, Throttled To Whole Percents
        var lastPercent = -1;
        var lastMicroseconds = -1L;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(firstProgress ?? s_stall);
        ProcessResult result;
        try
        {
            result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                arguments,
                s_timeout,
                stall.Token,
                line =>
                {
                    if (
                        !line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        || !long.TryParse(
                            line[12..],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var microseconds
                        )
                        || microseconds <= lastMicroseconds
                    )
                    {
                        return;
                    }

                    // Only Real Progress Keeps A Pass Alive
                    lastMicroseconds = microseconds;
                    stall.CancelAfter(s_stall);
                    var fraction = Math.Clamp(microseconds / 1e6 / duration, 0, 1);
                    var percent = (int)(fraction * 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(new ExportProgress(phase, offset + (fraction * share)));
                    }
                }
            );
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A Hung Driver Hands Over Like A Failed Encode
            throw new ExportException("The encoder stopped responding, try again");
        }

        if (!result.Succeeded)
        {
            throw new ExportException(
                result.LastError.Contains("No space", StringComparison.OrdinalIgnoreCase)
                    ? "The disk is full, free some space and try again"
                    : $"Export failed, {result.LastError}"
            );
        }
    }

    private static int EvenUp(double value)
    {
        return Math.Max(2, (int)Math.Ceiling(Math.Round(value, 6) / 2) * 2);
    }
}
