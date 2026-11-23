using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary
{
    // HEVC Matches The Picture With About Two Fifths Of Bits
    private const double ShrinkShare = 0.4;

    // Below This A Clip Is Already Small, Bits Per Second
    private const double ShrinkFloor = 4_000_000;

    // Clips Under A Week Old May Still Change, Off First
    public static readonly TimeSpan ShrinkAge = TimeSpan.FromDays(7);

    public static long? ShrunkBytesOf(ClipInfo clip)
    {
        // A Clip's Weight As HEVC, Null When Already Small
        return
            !clip.IsShrunk && clip.Duration > 1 && clip.SizeBytes * 8 / clip.Duration >= ShrinkFloor
            ? (long)(clip.SizeBytes * (ShrinkShare + 0.02))
            : null;
    }

    public static async Task<long> ShrinkAsync(
        string path,
        bool keepOriginal,
        IProgress<double> progress,
        CancellationToken token
    )
    {
        // HEVC At The Same Size, Rate And Frames, Sound Copied
        var source =
            await MediaProbe.ProbeAsync(path, token)
            ?? throw new InvalidDataException("The clip could not be read");
        var before = new FileInfo(path).Length;
        if (source.VideoCodec != "h264")
        {
            // Another Recorder's HEVC Or AV1 Is Small Already
            UpdateMetadata(path, metadata => metadata with { Shrunk = true });
            return 0;
        }

        var encoder =
            await HevcEncoderAsync(source, token)
            ?? throw new InvalidDataException("No graphics card can encode HEVC");
        var sound = source.AudioTrackCount * SizeBudget.AudioBitsPerSecond / 8 * source.Duration;
        var bitrate = Math.Max(1_000_000, (before - sound) * 8 / source.Duration * ShrinkShare);
        var temporary = PartialPathIn(Path.GetDirectoryName(path) ?? "");
        try
        {
            var rate = Math.Round(source.FrameRate is > 1 and < 241 ? source.FrameRate : 60);
            string Kilobits(double share) =>
                string.Create(CultureInfo.InvariantCulture, $"{(int)(bitrate * share / 1000)}k");
            await ClipExporter.RunPassAsync(
                [
                    "-hide_banner",
                    "-nostdin",
                    "-loglevel",
                    "error",
                    "-y",
                    "-progress",
                    "pipe:2",
                    "-nostats",
                    "-i",
                    path,
                    "-map",
                    "0:v:0",
                    "-map",
                    "0:a?",
                    "-map_metadata",
                    "0",
                    "-c:v",
                    encoder.Codec,
                    .. ClipExporter.QualityArguments(encoder),
                    // Keyframes Each Second, No B Frames, Cut Like A Recording
                    "-bf",
                    "0",
                    "-g",
                    rate.ToString(CultureInfo.InvariantCulture),
                    "-b:v",
                    Kilobits(1),
                    "-maxrate",
                    Kilobits(1.5),
                    "-bufsize",
                    Kilobits(2),
                    "-profile:v",
                    "main",
                    "-tag:v",
                    "hvc1",
                    .. VideoEncoders.ColourTags,
                    "-c:a",
                    "copy",
                    "-movflags",
                    "+faststart",
                    "-f",
                    "mp4",
                    temporary,
                ],
                source.Duration,
                $"Shrinking With {encoder.Name}",
                0,
                1,
                new Progress<ExportProgress>(step => progress.Report(step.Fraction)),
                token
            );

            // The Real File Is Checked, Never The Encoder's Promise
            var output = await MediaProbe.ProbeAsync(temporary, token);
            var after = new FileInfo(temporary).Length;
            if (
                output is not { VideoCodec: "hevc" }
                || Math.Abs(output.Duration - source.Duration) > 0.25
                || output.AudioTrackCount != source.AudioTrackCount
                || after >= before
            )
            {
                throw new InvalidDataException("The shrunk clip did not check out");
            }

            await ReplaceLikeCutAsync(path, temporary, keepOriginal);
            await ReplacedAsync(
                path,
                stored => stored with { Shrunk = true, ForeignOriginal = false },
                output.Duration
            );
            s_logger.Info(
                $"Shrank {Path.GetFileName(path)} From {Formatter.FormatSize(before)} To {Formatter.FormatSize(after)} With {encoder.Name}"
            );
            return before - after;
        }
        finally
        {
            await FFmpeg.DeleteOutputAsync(temporary);
        }
    }

    private static async Task<VideoEncoder?> HevcEncoderAsync(
        MediaInfo source,
        CancellationToken token
    )
    {
        // The Card's Own HEVC, Tested At The Clip's Size Once
        foreach (
            var candidate in VideoEncoders
                .Candidates()
                .Where(encoder => encoder.IsHardware && !encoder.GpuFrames)
                .Select(VideoEncoders.HevcOf)
        )
        {
            var rate = (int)Math.Round(source.FrameRate is > 1 and < 241 ? source.FrameRate : 60);
            if (await VideoEncoders.WorksAsync(candidate, source.Width, source.Height, rate, token))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task ReplaceLikeCutAsync(string path, string temporary, bool keepOriginal)
    {
        // The Whole Clip Aside Once, Recycled Unless Kept
        var original = OwnedOriginalOf(path);
        await ReplaceWhileBusyAsync(() =>
        {
            if (original is null)
            {
                KeepFullClip(path, temporary);
            }
            else
            {
                File.Move(temporary, path, overwrite: true);
            }
        });

        var originalPath = original ?? OriginalPathOf(path);
        if (!keepOriginal && Recycle(originalPath))
        {
            RemoveEmptyFolder(Path.GetDirectoryName(originalPath)!);
        }
    }
}
