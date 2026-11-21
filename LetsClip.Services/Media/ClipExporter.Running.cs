using System.Diagnostics;
using System.Globalization;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;

namespace LetsClip.Services.Media;

public static partial class ClipExporter
{
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

        // A Crop Always Encodes, Only The Part It Keeps
        var cropped = video && request.Crop is { } box && box.Changes(source.Width, source.Height);
        var (frameWidth, frameHeight, cropLeft, cropTop) = cropped
            ? request.Crop!.PixelsOf(source.Width, source.Height)
            : (source.Width, source.Height, 0, 0);

        // Game And Microphone Mixed Anew, Only From Their Own Tracks
        var balanced = request.IsBalanced && source.HasSeparateTracks;

        // Off, Or Small Enough, Copies Exactly, Sound Alone Only Off
        var copyRate = video ? sourceRate : SizeBudget.AudioBitsPerSecond / 8;
        if (
            !cropped
            && (video || request.Plan == DiscordPlan.Off)
            && SizeBudget.KeepsClip(request.Plan, duration, copyRate)
            && await CopyAsync(request, track, video, balanced, progress, token) is { } copied
        )
        {
            s_logger.Info(
                $"Exported {Path.GetFileName(copied)} {(balanced ? "With Its Picture Copied And Its Game And Microphone Mixed Anew" : "Losslessly")} At {Formatter.FormatSize(new FileInfo(copied).Length)}{(video ? "" : ", Sound Only,")} In {started.Elapsed.TotalSeconds:0.0} s"
            );
            return copied;
        }

        // Too Big For The Plan, Encoded Smaller To Fit
        var limit =
            SizeBudget.LimitOf(request.Plan)
            ?? CropBytes(sourceBytes, source.Duration, source, duration, request.Crop!) / 1e6;
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
        var gain = track is { } measured
            ? await LoudnessGainAsync(request, measured, balanced, progress, token)
            : 0;
        var workFolder = CacheFolders.NewFolder();
        var temporary = ClipLibrary.PartialPathIn(request.ExportsFolder);
        var crop = cropped
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"crop={frameWidth}:{frameHeight}:{cropLeft}:{cropTop},"
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
            video,
            gain,
            balanced
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
                        // The Next Estimate Takes This Export's Pace
                        if (video)
                        {
                            s_pixelsPerSecond =
                                width
                                * (double)height
                                * frameRate
                                * duration
                                / Math.Max(0.5, started.Elapsed.TotalSeconds - StartSeconds);
                        }

                        var finalPath = Keep(request, temporary, video ? ".mp4" : ".ogg");
                        var codec =
                            !video ? "Opus"
                            : encoder.IsHevc ? "HEVC"
                            : "H.264";
                        s_logger.Info(
                            $"Exported {Path.GetFileName(finalPath)} At {Formatter.FormatSize(size)} As {codec}{(video ? $" With {encoder.Name}" : "")}, Sound {gain:+0.0;-0.0;0} dB, In {attempts} Attempts And {started.Elapsed.TotalSeconds:0.0} s"
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
        bool balanced,
        IProgress<ExportProgress> progress,
        CancellationToken token
    )
    {
        // A Rebuilt Mix Is Measured, Evened Like Any Encode
        var gain = balanced ? await LoudnessGainAsync(request, 0, true, progress, token) : 0;

        // Stream Copied, The Clip's Own Frames, Size And Quality
        progress.Report(new ExportProgress("Copying", 0.5));
        var range = request.Range;
        var temporary = ClipLibrary.PartialPathIn(request.ExportsFolder);
        try
        {
            List<string> streams = video ? ["-map", "0:v:0"] : [];
            if (balanced)
            {
                streams.AddRange([
                    "-filter_complex",
                    SoundGraph(request, 0, true, gain),
                    "-map",
                    "[sound]",
                ]);
            }
            else if (track is { } index)
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
                token,
                balanced
                    ?
                    [
                        "-c:a",
                        "aac",
                        "-b:a",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{(int)(SizeBudget.AudioBitsPerSecond / 1000)}k"
                        ),
                    ]
                    : null
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
                : Keep(request, temporary, video ? ".mp4" : ".m4a");
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

    private static string Keep(ExportRequest request, string temporary, string extension)
    {
        // Named Like Its Clip, Never By Its Limit
        var finalPath = ClipSaver.UniquePath(
            request.ExportsFolder,
            Path.GetFileNameWithoutExtension(request.SourcePath),
            extension
        );
        File.Move(temporary, finalPath);
        return finalPath;
    }
}
