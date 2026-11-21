using System.ComponentModel;
using System.Globalization;
using LetsClip.Data.Helpers;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary
{
    private static Task<string?> ThumbnailAsync(string path, double duration)
    {
        // The Keyframe Two Seconds Before The Press, One Frame Decoded
        // Passed Through, So A Still Window's Last Frame Still Counts
        return RenderAsync(
            PicturePathOf(path, ThumbnailSuffix),
            [
                "-noaccurate_seek",
                "-skip_frame",
                "nokey",
                "-ss",
                FFmpeg.Seconds(Math.Max(duration / 2, duration - 2)),
                "-i",
                path,
                "-frames:v",
                "1",
                "-fps_mode",
                "passthrough",
                "-vf",
                "scale=480:-2",
                "-q:v",
                "4",
            ],
            TimeSpan.FromSeconds(30),
            CancellationToken.None
        );
    }

    public static Task<string?> EnsureStripAsync(
        string path,
        double duration,
        CancellationToken token,
        TrimRange? window = null
    )
    {
        // One Seek Per Cell, Twice As Fast As Decoding All
        var from = window?.Start ?? 0;
        var step = (window?.Length ?? Math.Max(duration - 1, 0.5)) / StripCells;
        var cells = Enumerable.Range(0, StripCells).ToList();
        var inputs = cells.SelectMany(cell =>
            new[]
            {
                "-threads",
                "1",
                "-noaccurate_seek",
                "-skip_frame",
                "nokey",
                "-ss",
                FFmpeg.Seconds(from + (cell * step)),
                "-i",
                path,
            }
        );
        var graph = string.Concat(
            cells.Select(cell =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{cell}:v]trim=end_frame=1,setpts=PTS-STARTPTS,scale=-2:{StripHeight}[c{cell}];"
                )
            )
        );
        var stack =
            string.Concat(cells.Select(cell => $"[c{cell}]")) + $"hstack=inputs={StripCells}";
        return RenderAsync(
            PicturePathOf(
                path,
                window is { } zoomed ? WindowSuffix(ZoomMark, zoomed) : s_stripSuffix
            ),
            [.. inputs, "-filter_complex", graph + stack, "-frames:v", "1", "-q:v", "5"],
            TimeSpan.FromSeconds(60),
            token
        );
    }

    private static async Task<string?> RenderAsync(
        string target,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken token
    )
    {
        if (File.Exists(target))
        {
            return target;
        }

        // Each Run Writes Its Own Temporary File
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp.jpg";
        try
        {
            Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. arguments, temporary],
                timeout,
                token
            );
            if (!result.Succeeded || !File.Exists(temporary))
            {
                return null;
            }

            File.Move(temporary, target, overwrite: true);
            DeleteOlderPictures(target);
            return target;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or TimeoutException
                        or Win32Exception
            )
        {
            // A Picture Only Decorates, Its Clip Shows Without It
            s_logger.Warn($"Failed To Make A Picture {exception.GetType().Name}");
            return null;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static Task<string?> EnsureWaveAsync(
        string path,
        double duration,
        bool separate,
        CancellationToken token,
        TrimRange? window = null
    )
    {
        // White On Black, The Page Lays It Over The Strip
        var (from, to) = window is { } zoomed ? (zoomed.Start, zoomed.End) : (0, duration);

        // A Late Window Seeks Just Before, The Trim Stays Exact
        var seek = from > WaveLead ? from - WaveLead : 0;
        IReadOnlyList<string> input = seek > 0 ? ["-ss", FFmpeg.Seconds(seek)] : [];
        var outline =
            $"atrim={FFmpeg.Seconds(from - seek)}:{FFmpeg.Seconds(to - seek)},showwavespic=s={WaveSize}:scale=sqrt:draw=full:filter=peak";

        // Separate Tracks Draw The Game White, The Microphone Grey
        var graph = separate
            ? $"[0:a:1]{outline}:colors=white[game];[0:a:2]{outline}:colors={VoiceColor}[voice];[game][voice]overlay"
            : $"[0:a:0]{outline}:colors=white";
        return RenderAsync(
            PicturePathOf(path, window is { } part ? WindowSuffix(ZoomWaveMark, part) : WaveSuffix),
            [.. input, "-i", path, "-filter_complex", graph, "-frames:v", "1", "-q:v", "3"],
            TimeSpan.FromSeconds(60),
            token
        );
    }

    private static string WindowSuffix(string mark, TrimRange window)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{mark}{window.Start:0.###}-{window.End:0.###}.jpg"
        );
    }

    private static string KindOf(string picture)
    {
        return picture.EndsWith(s_stripSuffix, StringComparison.Ordinal) ? s_stripSuffix
            : picture.EndsWith(WaveSuffix, StringComparison.Ordinal) ? WaveSuffix
            : picture.Contains(ZoomWaveMark, StringComparison.Ordinal) ? ZoomWaveMark
            : picture.Contains(ZoomMark, StringComparison.Ordinal) ? ZoomMark
            : ThumbnailSuffix;
    }

    private static void DeleteOlderPictures(string target)
    {
        // Other Generations Of This Clip's Same Kind Of Picture
        var kind = KindOf(target);
        var name = Path.GetFileName(target);
        foreach (var older in PicturesOf(name[..FilePaths.KeyLength]))
        {
            if (older != target && KindOf(older) == kind)
            {
                TryDelete(older);
            }
        }
    }

    private static IEnumerable<string> PicturesOf(string key)
    {
        // Pictures A Running Render Still Writes Are Left Alone
        return Directory
            .EnumerateFiles(AppInfo.ThumbnailsDirectory, $"{key}*.jpg")
            .Where(file => !file.EndsWith(".tmp.jpg", StringComparison.Ordinal));
    }

    private static string PicturePathOf(string path, string suffix)
    {
        // Named By The File's Generation, Replaced Files Get New Ones
        var file = new FileInfo(path);
        var generation = file.Exists ? $"-{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}" : "";
        return Path.Combine(
            AppInfo.ThumbnailsDirectory,
            $"{FilePaths.KeyOf(path)}{generation}{suffix}"
        );
    }
}
