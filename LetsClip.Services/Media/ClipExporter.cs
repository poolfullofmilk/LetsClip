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
    string ExportsFolder,
    bool CopyWhenDone = false,
    double? Crop = null,
    bool Video = true
);

public sealed record ExportProgress(string Phase, double Fraction);

public sealed class ExportException(string message) : Exception(message);

// Neither Means The Clip Itself Already Fits
public sealed record DiscordCopy(ExportRequest? Request, string? Problem);

public static class ClipExporter
{
    // A Shorter Result Means Part Of The Selection Was Lost
    private const double DurationTolerance = 0.25;

    // A Third Try Follows The Line The First Two Drew
    private const int AttemptsPerEncoder = 3;

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

    static ClipExporter()
    {
        Debug.Assert(SelfTestPasses());
    }

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

    internal static double NextBitrate(
        IReadOnlyList<(double Bitrate, long Size)> tries,
        long target
    )
    {
        // The Miss Plus Margin, Then Along Two Tries' Line
        var (bitrate, size) = tries[^1];
        var proportional = bitrate * target / size * 0.97;
        if (tries.Count < 2)
        {
            return proportional;
        }

        // Quick Sync Barely Shrank At Lower Bitrates, Step Harder
        var (earlierBitrate, earlierSize) = tries[^2];
        var bytesPerBit = (size - earlierSize) / (bitrate - earlierBitrate);
        var aimed =
            bytesPerBit > 0 ? bitrate - ((size - (target * 0.97)) / bytesPerBit) : bitrate / 2;
        return Math.Clamp(aimed, bitrate / 2, proportional);
    }

    public static (int Width, int Height) CropSizeOf(int height)
    {
        // The Tallest 9 By 16 Part Of The Frame, Even
        return ((int)(height * 9 / 16.0) / 2 * 2, height / 2 * 2);
    }

    public static long VerticalBytes(
        long clipBytes,
        double clipSeconds,
        MediaInfo media,
        double seconds
    )
    {
        // Off Keeps The Clip's Bits Per Pixel Over The Crop
        var (width, height) = CropSizeOf(media.Height);
        var kept = width * height / (double)(media.Width * media.Height);
        return (long)(clipBytes / clipSeconds * seconds * kept);
    }

    public static double FrameRateOf(MediaInfo media, bool encodes)
    {
        // Odd Probes Read As 60, An Encode Stops At 60
        var rate = media.FrameRate is > 1 and < 241 ? Math.Round(media.FrameRate) : 60;
        return encodes ? Math.Min(rate, 60) : rate;
    }

    public static int? TrackOf(MediaInfo media, bool game, bool microphone)
    {
        // Separate Tracks Can Leave Out Game Or Voice
        var keepsGame = game && media.HasAudio;
        return media.HasSeparateTracks ? AudioTracks.IndexOf(keepsGame, microphone)
            : keepsGame ? 0
            : null;
    }

    public static bool KeepsVideo(MediaInfo media, bool video, bool game, bool microphone)
    {
        // Without Sound Kept, The Video Stays, Never Nothing
        return video || TrackOf(media, game, microphone) is null;
    }

    public static long CopyBytes(
        long clipBytes,
        MediaInfo media,
        TrimRange range,
        int tracks,
        bool video = true
    )
    {
        // The Whole Clip Is The File, A Part Never More
        if (video && !range.CutsShort(media.Duration) && tracks == media.AudioTrackCount)
        {
            return clipBytes;
        }

        // Without Packets, A Share Of The File Or Usual Sound
        var estimate = video
            ? clipBytes * range.Length / media.Duration
            : tracks * range.Length * SizeBudget.AudioBitsPerSecond / 8;
        return Math.Min(
            clipBytes,
            media.Packets is { } packets
                ? SizeBudget.CopyBytes(packets, range, tracks, video)
                : (long)estimate
        );
    }

    public static DiscordCopy ForDiscord(
        ClipInfo clip,
        MediaInfo media,
        TrimRange range,
        DiscordPlan plan,
        bool game,
        bool microphone,
        bool video,
        string folder,
        double? crop
    )
    {
        // The Editor's Plan Rules, Pressed From A Card, Crop Included
        var track = TrackOf(media, game, microphone);
        var keepsVideo = KeepsVideo(media, video, game, microphone);
        var limit = SizeBudget.LimitOf(plan) ?? double.MaxValue;
        var cropped = keepsVideo && crop is not null && media.Width > media.Height;
        var copied = CopyBytes(clip.SizeBytes, media, range, track is null ? 0 : 1, keepsVideo);
        var encodes = cropped || copied > limit * 1e6;
        var everyTrack = media.HasSeparateTracks ? track == 0 : track is not null == media.HasAudio;
        if (!encodes && keepsVideo && everyTrack && !range.CutsShort(media.Duration))
        {
            return new(null, null);
        }

        var frameRate = FrameRateOf(media, encodes);
        var (frameWidth, frameHeight) = cropped
            ? CropSizeOf(media.Height)
            : (media.Width, media.Height);
        var (width, height) = OutputSize(frameWidth, frameHeight);
        var shape = new ExportShape(
            range.Length,
            width,
            height,
            frameRate,
            track is not null,
            keepsVideo
        );
        if (encodes && SizeBudget.Plan(limit, shape).Verdict == BudgetVerdict.TooSmall)
        {
            return new(
                null,
                $"Too long for {SizeBudget.NameOf(plan)}, which fits about {Formatter.FormatDuration(SizeBudget.LongestSeconds(limit, shape))}, open it to pick a shorter selection"
            );
        }

        return new(
            new ExportRequest(
                clip.Path,
                range,
                encodes ? plan : DiscordPlan.Off,
                frameRate,
                track,
                folder,
                CopyWhenDone: true,
                Crop: cropped ? crop : null,
                Video: keepsVideo
            ),
            null
        );
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
        var sourceBytes = new FileInfo(request.SourcePath).Length;
        var sourceRate = sourceBytes / source.Duration;
        Directory.CreateDirectory(request.ExportsFolder);
        var started = Stopwatch.StartNew();

        // Video Off Keeps The Sound Alone, Never Nothing
        var video = request.Video || !withAudio;

        // A Vertical Crop Always Encodes, Only The Part It Keeps
        var cropped = video && request.Crop is not null && source.Width > source.Height;
        var (frameWidth, frameHeight) = cropped
            ? CropSizeOf(source.Height)
            : (source.Width, source.Height);

        // Off, Or Already Small Enough, Is An Exact Copy
        var copyRate = video ? sourceRate : SizeBudget.AudioBitsPerSecond / 8;
        if (
            !cropped
            && SizeBudget.KeepsClip(request.Plan, duration, copyRate)
            && await CopyAsync(request, track, video, progress, token) is { } copied
        )
        {
            s_logger.Info(
                $"Exported {Path.GetFileName(copied)} Losslessly At {Formatter.FormatSize(new FileInfo(copied).Length)}{(video ? "" : ", Sound Only,")} In {started.Elapsed.TotalSeconds:0.0} s"
            );
            return copied;
        }

        // Too Big For The Plan, Encoded Smaller To Fit
        var limit =
            SizeBudget.LimitOf(request.Plan)
            ?? VerticalBytes(sourceBytes, source.Duration, source, duration) / 1e6;
        var (fullWidth, fullHeight) = OutputSize(frameWidth, frameHeight);
        var clip = new ExportShape(
            duration,
            fullWidth,
            fullHeight,
            request.FrameRate,
            withAudio,
            video
        );
        var budget = SizeBudget.Plan(limit, clip);
        var (width, height) = OutputSize(frameWidth, frameHeight, budget.Lines);
        var frameRate = Math.Min(request.FrameRate, budget.FrameRate);
        var shown = video
            ? $"At {width} × {height}, {frameRate} FPS, {(withAudio ? $"With {budget.AudioBitsPerSecond / 1000:0} kbps Audio" : "Without Audio")}"
            : $"As Sound Only At {budget.AudioBitsPerSecond / 1000:0} kbps";
        s_logger.Info(
            $"Exporting {Path.GetFileName(request.SourcePath)} From {Formatter.FormatDuration(request.Range.Start)} To {Formatter.FormatDuration(request.Range.End)} {shown}, Aiming Under {Formatter.FormatSize((long)(limit * 1_000_000))}, {budget.Verdict}"
        );
        if (budget.Verdict == BudgetVerdict.TooSmall)
        {
            throw new ExportException(TooSmallMessage);
        }

        // HEVC On The Card, Then Its H.264, x264 Last
        var encoders = new List<VideoEncoder>();
        var hardware = VideoEncoders
            .Candidates()
            .Where(candidate => video && candidate.IsHardware && !candidate.GpuFrames)
            .ToList();
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
        var crop = cropped
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"crop={frameWidth}:{frameHeight}:{(int)((source.Width - frameWidth) * request.Crop!.Value) / 2 * 2}:0,"
            )
            : "";
        var job = new EncodeJob(
            request,
            temporary,
            workFolder,
            width,
            height,
            frameRate,
            track,
            crop,
            budget.AudioBitsPerSecond,
            video
        );
        try
        {
            var attempts = 0;
            foreach (var encoder in encoders)
            {
                // Sound Alone Retries On Its Own Bitrate
                var bitrate = !video
                    ? budget.AudioBitsPerSecond
                    : budget.VideoBitsPerSecond * (encoder.IsHardware ? HardwareAim : 1);
                List<(double Bitrate, long Size)> tries = [];
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
                        output is null
                        || output.HasVideo != video
                        || (video && output.VideoCodec != (encoder.IsHevc ? "hevc" : "h264"))
                        || (video && output.PixelFormat != "yuv420p")
                        || !LengthHolds(output.Duration, duration, video)
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
                        var codec =
                            !video ? "AAC"
                            : encoder.IsHevc ? "HEVC"
                            : "H.264";
                        s_logger.Info(
                            $"Exported {Path.GetFileName(finalPath)} At {Formatter.FormatSize(size)} As {codec} With {encoder.Name} In {attempts} Attempts And {started.Elapsed.TotalSeconds:0.0} s"
                        );
                        return finalPath;
                    }

                    // Over The Limit, Retry Lower By What The Encoder Showed
                    s_logger.Info(
                        $"Export came out at {Formatter.FormatSize(size)}, retrying smaller"
                    );
                    tries.Add((bitrate, size));
                    bitrate = NextBitrate(tries, budget.TargetBytes);
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
        bool video,
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
            List<string> streams = video ? ["-map", "0:v:0"] : [];
            if (track is { } index)
            {
                streams.AddRange([
                    "-map",
                    string.Create(CultureInfo.InvariantCulture, $"0:a:{index}"),
                ]);
            }

            var output = await ClipLibrary.CopyRangeAsync(
                request.SourcePath,
                range,
                streams,
                temporary,
                token
            );
            if (
                output is null
                || output.HasVideo != video
                || !LengthHolds(output.Duration, range.Length, video)
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

    private static bool LengthHolds(double written, double asked, bool video)
    {
        // Sound Alone May End Before A Window's Held Last Frame
        return video
            ? Math.Abs(written - asked) <= DurationTolerance
            : written - asked <= DurationTolerance;
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
        int? AudioTrack,
        string Crop,
        double AudioBitsPerSecond,
        bool Video
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
            // Sound Alone Retries Its Own Bitrate, Else The Budget's
            var sound = job.Video ? job.AudioBitsPerSecond : bitrate;
            output.AddRange([
                "-map",
                string.Create(CultureInfo.InvariantCulture, $"0:a:{track}"),
                "-c:a",
                "aac",
                "-b:a",
                string.Create(CultureInfo.InvariantCulture, $"{(int)(sound / 1000)}k"),
                "-ac",
                sound < SizeBudget.StereoBitsPerSecond ? "1" : "2",
                "-ar",
                "48000",
            ]);
        }

        output.AddRange(["-movflags", "+faststart", "-f", "mp4", job.Output]);
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

    private static bool SelfTestPasses()
    {
        // Quick Sync's Measured Miss, 51.4 Then 50.7 MB For 47.5
        var first = NextBitrate([(10_000_000, 51_400_000)], 47_500_000);
        var second = NextBitrate([(10_000_000, 51_400_000), (first, 50_700_000)], 47_500_000);
        return Math.Abs(first - (10_000_000 * 47.5 / 51.4 * 0.97)) < 1
            && second < first * 0.75
            && second >= first / 2
            && NextBitrate([(10_000_000, 30_000_000), (9_000_000, 31_000_000)], 20_000_000)
                == 4_500_000
            && FrameRateOf(new MediaInfo(5, "h264", null, 0, 0, 239.9, null, null), true) == 60
            && FrameRateOf(new MediaInfo(5, "h264", null, 0, 0, 0, null, null), false) == 60;
    }
}
