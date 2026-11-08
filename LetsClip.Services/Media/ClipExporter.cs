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
    double Megabytes,
    double FrameRate,
    bool WithAudio,
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

    public const string TooSmallMessage =
        "Too little room for this length, pick a larger size or a shorter selection";

    public static (int Width, int Height) OutputSize(int width, int height, int lines = 1080)
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

    public static double FrameRateOf(double sourceRate, bool fullFrameRate)
    {
        // Odd Probes Read As 60, Faster Clips Drop To 60
        var rate = sourceRate is > 1 and < 241 ? Math.Round(sourceRate) : 60;
        return fullFrameRate ? rate : Math.Min(rate, 60);
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

        if (!SizeBudget.IsValidTarget(request.Megabytes))
        {
            throw new ExportException(
                $"Pick a size between {SizeBudget.MinimumMegabytes} and {SizeBudget.MaximumMegabytes} MB"
            );
        }

        // Past The Ceiling Only Bloats, Unlimited Lands There
        var duration = request.Range.Length;
        var withAudio = request.WithAudio && source.HasAudio;
        var (width, height) = OutputSize(source.Width, source.Height);
        var frameRate = request.FrameRate;
        var sourceRate = new FileInfo(request.SourcePath).Length / source.Duration;
        var clip = new ExportShape(duration, width, height, frameRate, withAudio, sourceRate);
        request = request with
        {
            Megabytes = Math.Min(request.Megabytes, SizeBudget.CeilingMegabytes(clip)),
        };
        var budget = SizeBudget.Plan(request.Megabytes, clip);
        var started = Stopwatch.StartNew();
        s_logger.Info(
            $"Exporting {Path.GetFileName(request.SourcePath)} From {Formatter.FormatDuration(request.Range.Start)} To {Formatter.FormatDuration(request.Range.End)} At {width} × {height}, {frameRate} FPS, {(withAudio ? "With Audio" : "Without Audio")}, Aiming Under {Formatter.FormatMegabytes(request.Megabytes)}, {budget.Verdict}"
        );
        if (budget.Verdict == BudgetVerdict.TooSmall)
        {
            throw new ExportException(TooSmallMessage);
        }

        // The Graphics Card Measured Faster And Sharper, x264 Falls Back
        var encoders = new List<VideoEncoder>();
        foreach (var candidate in VideoEncoders.Candidates(MonitorCatalog.AdapterVendors()))
        {
            if (
                candidate.IsHardware
                && await VideoEncoders.WorksAsync(candidate, width, height, (int)frameRate, token)
            )
            {
                encoders.Add(candidate);
                break;
            }
        }

        encoders.Add(VideoEncoders.Software);
        Directory.CreateDirectory(request.ExportsFolder);
        var workFolder = CacheFolders.NewFolder();
        var temporary = Path.Combine(
            request.ExportsFolder,
            $"{ClipLibrary.PartialPrefix}{Guid.NewGuid():N}.mp4"
        );
        var job = new EncodeJob(
            request,
            temporary,
            workFolder,
            width,
            height,
            frameRate,
            withAudio
        );
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
                        || output.VideoCodec != "h264"
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

                        throw new ExportException(
                            "The exported file did not check out, nothing was kept"
                        );
                    }

                    if (size <= budget.TargetBytes)
                    {
                        // Named By The Clip's Name Or Date, Never Its Limit
                        var stem = Path.GetFileNameWithoutExtension(request.SourcePath);
                        var title =
                            ClipNames.FileSafe(ClipLibrary.MetadataOf(request.SourcePath)?.Title)
                            ?? (
                                ClipNames.DateOf(stem) is { } date
                                    ? $"{AppInfo.DisplayName} {Formatter.FormatFileDate(date)}"
                                    : stem
                            );
                        var finalPath = ClipSaver.UniquePath(request.ExportsFolder, title);
                        File.Move(temporary, finalPath);
                        s_logger.Info(
                            $"Exported {Path.GetFileName(finalPath)} At {Formatter.FormatSize(size)} With {encoder.Name} In {attempts} Attempts And {started.Elapsed.TotalSeconds:0.0} s"
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

            throw new ExportException(
                "Could not get under the size limit, pick a larger size or a shorter selection"
            );
        }
        finally
        {
            // Only This Export's Own Files, The Source Is Never Touched
            await FFmpeg.DeleteOutputAsync(temporary);
            CacheFolders.Delete(workFolder);
        }
    }

    private sealed record EncodeJob(
        ExportRequest Request,
        string Output,
        string WorkFolder,
        int Width,
        int Height,
        double FrameRate,
        bool WithAudio
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
        if (job.WithAudio)
        {
            output.AddRange([
                "-map",
                "0:a:0",
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
        var label = attempt == 1 ? "" : $", Try {attempt}";
        if (encoder.IsHardware)
        {
            await RunPassAsync(
                [.. input, .. output],
                range.Length,
                $"Encoding on {encoder.Name}{label}",
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
            $"Analysing{label}",
            0,
            0.5,
            progress,
            token
        );
        await RunPassAsync(
            [.. input, "-passlogfile", passLog, "-pass", "2", .. output],
            range.Length,
            $"Encoding{label}",
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
                "-preanalysis",
                "1",
            ],
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
            "h264_qsv" => ["-preset", "slower", "-bf", "3"],
            _ => ["-preset", "medium"],
        };
        return ["-c:v", encoder.Codec, .. specific, "-profile:v", "high", .. rate];
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
