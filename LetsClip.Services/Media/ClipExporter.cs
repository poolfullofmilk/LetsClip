using System.Diagnostics;
using System.Globalization;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using NLog;

namespace LetsClip.Services.Media;

public sealed record ExportRequest(
    string SourcePath,
    TrimRange Range,
    DiscordPlan Plan,
    double FrameRate,
    int? AudioTrack,
    string ExportsFolder
);

public sealed record ExportProgress(string Phase, double Fraction);

public sealed class ExportException(string message) : Exception(message);

public static class ClipExporter
{
    // A Shorter Result Means Part Of The Selection Was Lost
    private const double DurationTolerance = 0.25;
    private const int AttemptsPerEncoder = 2;

    // Single Pass AMF Overshot Its Average By 5 To 8%
    private const double HardwareAim = 0.9;

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_stall = TimeSpan.FromSeconds(15);

    // AMF Shows Progress Within A Second, A Silent Start Hung
    private static readonly TimeSpan s_hardwareStart = TimeSpan.FromSeconds(5);

    private const string TooSmallMessage =
        "Too long for this Discord plan, pick a shorter selection";

    private const string RejectedMessage = "The exported file did not check out, nothing was kept";

    public static (int Width, int Height) OutputSize(
        int width,
        int height,
        int lines = SizeBudget.FullLines
    )
    {
        // A 16 By 9 Box In Its Orientation, Never Upscaled
        var portrait = height > width;
        var across = lines * 16 / 9.0;
        var scale = Math.Min(
            1,
            Math.Min((portrait ? lines : across) / width, (portrait ? across : lines) / height)
        );
        return (EvenUp(width * scale), EvenUp(height * scale));
    }

    public static async Task<string> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress> progress,
        CancellationToken token
    )
    {
        // Each Export Writes Its Own Temporary File And Work Folder
        try
        {
            return await RunAsync(request, progress, token);
        }
        catch (ExportException exception)
        {
            s_logger.Warn($"Failed To Export, {exception.Message}");
            throw;
        }
        catch (OperationCanceledException)
        {
            s_logger.Info("Cancelled An Export");
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            s_logger.Warn("Failed To Export, The Exports Folder Refused Writing");
            throw new ExportException("Cannot write to the exports folder");
        }
        catch (IOException exception)
        {
            s_logger.Warn($"Failed To Export {exception.GetType().Name}");
            throw new ExportException("Export failed, the file could not be written");
        }
    }

    private static async Task<string> RunAsync(
        ExportRequest request,
        IProgress<ExportProgress> progress,
        CancellationToken token
    )
    {
        progress.Report(new ExportProgress("Reading Clip", 0));
        var source =
            await MediaProbe.ProbeAsync(request.SourcePath, token)
            ?? throw new ExportException("The clip could not be read, it may have moved");
        if (!source.HasVideo || !request.Range.IsValidFor(source.Duration))
        {
            throw new ExportException("The selection does not fit inside the clip");
        }

        var duration = request.Range.Length;
        // The Picked Track, The First Where Only One Exists
        int? track =
            source.HasAudio && request.AudioTrack is { } picked
                ? picked < source.AudioTrackCount
                    ? picked
                    : 0
                : null;
        var withAudio = track is not null;
        var sourceRate = new FileInfo(request.SourcePath).Length / source.Duration;
        Directory.CreateDirectory(request.ExportsFolder);
        var started = Stopwatch.StartNew();

        // Off, Or Already Small Enough, Is An Exact Copy
        if (
            SizeBudget.KeepsClip(request.Plan, duration, sourceRate)
            && await CopyAsync(request, track, progress, token) is { } copied
        )
        {
            s_logger.Info(
                $"Exported {Path.GetFileName(copied)} Losslessly At {Formatter.FormatSize(new FileInfo(copied).Length)} In {started.Elapsed.TotalSeconds:0.0} s"
            );
            return copied;
        }

        // Too Big For The Plan, Encoded Smaller To Fit
        var limit = SizeBudget.LimitOf(request.Plan)!.Value;
        var (fullWidth, fullHeight) = OutputSize(source.Width, source.Height);
        var frameRate = request.FrameRate;
        var clip = new ExportShape(duration, fullWidth, fullHeight, frameRate, withAudio);
        var budget = SizeBudget.Plan(limit, clip);
        var (width, height) = OutputSize(source.Width, source.Height, budget.Lines);
        s_logger.Info(
            $"Exporting {Path.GetFileName(request.SourcePath)} From {Formatter.FormatDuration(request.Range.Start)} To {Formatter.FormatDuration(request.Range.End)} At {width} × {height}, {frameRate} FPS, {(withAudio ? "With Audio" : "Without Audio")}, Aiming Under {Formatter.FormatSize((long)(limit * 1_000_000))}, {budget.Verdict}"
        );
        if (budget.Verdict == BudgetVerdict.TooSmall)
        {
            throw new ExportException(TooSmallMessage);
        }

        // HEVC On The Graphics Card, Then Its H.264, x264 Last
        var hardware = VideoEncoders
            .Candidates()
            .Where(candidate => candidate.IsHardware && !candidate.GpuFrames)
            .ToList();
        var encoders = new List<VideoEncoder>();
        foreach (var family in new[] { hardware.Select(VideoEncoders.HevcOf), hardware })
        {
            foreach (var candidate in family)
            {
                if (await VideoEncoders.WorksAsync(candidate, width, height, (int)frameRate, token))
                {
                    encoders.Add(candidate);
                    break;
                }
            }
        }

        encoders.Add(VideoEncoders.Software);
        var workFolder = CacheFolders.NewFolder();
        var temporary = ClipLibrary.PartialPathIn(request.ExportsFolder);
        var job = new EncodeJob(request, temporary, workFolder, width, height, frameRate, track);
        try
        {
            var attempts = 0;
            foreach (var encoder in encoders)
            {
                var bitrate = budget.VideoBitsPerSecond * (encoder.IsHardware ? HardwareAim : 1);
                var restarted = false;
                for (var attempt = 1; attempt <= AttemptsPerEncoder; attempt++)
                {
                    attempts++;
                    try
                    {
                        await EncodeAsync(job, encoder, bitrate, attempts, progress, token);
                    }
                    catch (ExportException exception) when (encoder.IsHardware && !restarted)
                    {
                        // AMF Can Fail Starting Right After Another Session
                        s_logger.Warn($"Restarted Export With {encoder.Name}, {exception.Message}");
                        restarted = true;
                        attempt--;
                        continue;
                    }
                    catch (ExportException exception) when (encoder.IsHardware)
                    {
                        // Failing Twice Hands Over To x264
                        s_logger.Warn($"Failed To Export With {encoder.Name}, {exception.Message}");
                        break;
                    }

                    // The Real File Is Checked, Never The Encoder's Promise
                    progress.Report(new ExportProgress("Checking", 1));
                    var size = new FileInfo(temporary).Length;
                    var output = await MediaProbe.ProbeAsync(temporary, token);
                    if (
                        output is not { HasVideo: true }
                        || output.VideoCodec != (encoder.IsHevc ? "hevc" : "h264")
                        || output.PixelFormat != "yuv420p"
                        || Math.Abs(output.Duration - duration) > DurationTolerance
                        || output.HasAudio != withAudio
                    )
                    {
                        if (encoder.IsHardware)
                        {
                            s_logger.Warn($"Rejected Export From {encoder.Name}");
                            break;
                        }

                        throw new ExportException(RejectedMessage);
                    }

                    if (size <= budget.TargetBytes)
                    {
                        var finalPath = Keep(request, temporary);
                        s_logger.Info(
                            $"Exported {Path.GetFileName(finalPath)} At {Formatter.FormatSize(size)} As {(encoder.IsHevc ? "HEVC" : "H.264")} With {encoder.Name} In {attempts} Attempts And {started.Elapsed.TotalSeconds:0.0} s"
                        );
                        return finalPath;
                    }

                    // Over The Limit, Retry Lower By The Miss Plus Margin
                    s_logger.Info(
                        $"Export came out at {Formatter.FormatSize(size)}, retrying smaller"
                    );
                    bitrate *= budget.TargetBytes / (double)size * 0.97;
                }
            }

            throw new ExportException("Could not fit this Discord plan, pick a shorter selection");
        }
        finally
        {
            // Only This Export's Own Files, The Source Is Never Touched
            await FFmpeg.DeleteOutputAsync(temporary);
            CacheFolders.Delete(workFolder);
        }
    }

    private static async Task<string?> CopyAsync(
        ExportRequest request,
        int? track,
        IProgress<ExportProgress> progress,
        CancellationToken token
    )
    {
        // Stream Copied, The Clip's Own Frames, Size And Quality
        progress.Report(new ExportProgress("Copying", 0.5));
        var range = request.Range;
        var temporary = ClipLibrary.PartialPathIn(request.ExportsFolder);
        try
        {
            var output = await ClipLibrary.CopyRangeAsync(
                request.SourcePath,
                range,
                track is { } index
                    ?
                    [
                        "-map",
                        "0:v:0",
                        "-map",
                        string.Create(CultureInfo.InvariantCulture, $"0:a:{index}"),
                    ]
                    : ["-map", "0:v:0"],
                temporary,
                token
            );
            if (
                output is not { HasVideo: true }
                || Math.Abs(output.Duration - range.Length) > DurationTolerance
                || output.HasAudio != (track is not null)
            )
            {
                throw new ExportException(RejectedMessage);
            }

            // The Plan's Limit Holds, Past It Encoding Takes Over
            return
                SizeBudget.LimitOf(request.Plan) is { } limit
                && new FileInfo(temporary).Length > limit * 1_000_000
                ? null
                : Keep(request, temporary);
        }
        finally
        {
            await FFmpeg.DeleteOutputAsync(temporary);
        }
    }

    private static string Keep(ExportRequest request, string temporary)
    {
        // Named Like Its Clip, Never By Its Limit
        var finalPath = ClipSaver.UniquePath(
            request.ExportsFolder,
            Path.GetFileNameWithoutExtension(request.SourcePath)
        );
        File.Move(temporary, finalPath);
        return finalPath;
    }

    private sealed record EncodeJob(
        ExportRequest Request,
        string Output,
        string WorkFolder,
        int Width,
        int Height,
        double FrameRate,
        int? AudioTrack
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
            $"scale={job.Width}:{job.Height}:force_original_aspect_ratio=decrease:flags=lanczos,pad={job.Width}:{job.Height}:-1:-1:color=black,setsar=1,format=yuv420p"
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
            "-map",
            "0:v:0",
            "-vf",
            filter,
            "-r",
            rate,
            "-fps_mode",
            "cfr",
            .. VideoArguments(encoder, bitrate, job.FrameRate),
            "-colorspace",
            "bt709",
            "-color_primaries",
            "bt709",
            "-color_trc",
            "bt709",
            "-color_range",
            "tv",
        ];
        List<string> output = [];
        if (job.AudioTrack is { } track)
        {
            output.AddRange([
                "-map",
                string.Create(CultureInfo.InvariantCulture, $"0:a:{track}"),
                "-c:a",
                "aac",
                "-b:a",
                "128k",
                "-ac",
                "2",
                "-ar",
                "48000",
            ]);
        }

        output.AddRange(["-movflags", "+faststart", "-f", "mp4", job.Output]);
        var label = attempt == 1 ? "" : $"Try {attempt}, ";
        if (encoder.IsHardware)
        {
            await RunPassAsync(
                [.. input, .. output],
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
            [.. input, "-passlogfile", passLog, "-pass", "1", "-an", "-f", "null", "NUL"],
            range.Length,
            $"{label}Analysing",
            0,
            0.5,
            progress,
            token
        );
        await RunPassAsync(
            [.. input, "-passlogfile", passLog, "-pass", "2", .. output],
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
        List<string> specific = encoder.Codec switch
        {
            "h264_amf" =>
            [
                "-usage",
                "transcoding",
                "-quality",
                "quality",
                "-rc",
                "vbr_peak",
                "-bf",
                "3",
            ],
            "hevc_amf" => ["-usage", "transcoding", "-quality", "quality", "-rc", "vbr_peak"],
            "h264_nvenc" =>
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
                "-bf",
                "3",
            ],
            "hevc_nvenc" =>
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
            "h264_qsv" => ["-preset", "slower", "-bf", "3"],
            "hevc_qsv" => ["-preset", "slower"],
            _ => ["-preset", "medium"],
        };
        // Tagged hvc1, So Apple Devices Play It Too
        string[] profile = encoder.IsHevc
            ? ["-profile:v", "main", "-tag:v", "hvc1"]
            : ["-profile:v", "high"];
        return ["-c:v", encoder.Codec, .. specific, .. profile, .. rate];
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
