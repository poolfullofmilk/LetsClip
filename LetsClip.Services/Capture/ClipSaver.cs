using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Media;

namespace LetsClip.Services.Capture;

public static class ClipSaver
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    public static async Task<ClipInfo> SaveAsync(
        IReadOnlyList<Segment> video,
        IReadOnlyList<Segment> audio,
        double keepFrom,
        double keepTo,
        string sessionFolder,
        string clipsFolder,
        string? monitorName,
        DateTimeOffset recordedAt,
        ClipLibrary library,
        CancellationToken token
    )
    {
        Directory.CreateDirectory(clipsFolder);

        // Where The Copied Media Really Starts, Keyframe Pre-Roll Included
        var firstVideo =
            await MediaProbe.ProbeAsync(video[0].Path, token)
            ?? throw new InvalidDataException("Failed To Read The Replay Cache");
        var firstAudio = audio.Count > 0 ? await MediaProbe.ProbeAsync(audio[0].Path, token) : null;
        var fileStart = Math.Min(firstVideo.StartTime, firstAudio?.StartTime ?? double.MaxValue);

        var temporary = Path.Combine(
            clipsFolder,
            $"{ClipLibrary.PartialPrefix}{Guid.NewGuid():N}.mp4"
        );
        try
        {
            // Stream Copy Up To The Press, Joined Segments Keep Time
            List<string> arguments =
            [
                "-hide_banner",
                "-nostdin",
                "-loglevel",
                "error",
                "-y",
                "-copyts",
                "-i",
                ConcatOf(video),
            ];
            if (audio.Count > 0)
            {
                arguments.AddRange(["-i", ConcatOf(audio), "-map", "0:v", "-map", "1:a"]);
            }
            else
            {
                arguments.AddRange(["-map", "0:v"]);
            }

            arguments.AddRange([
                "-c",
                "copy",
                "-to",
                Seconds(keepTo),
                "-avoid_negative_ts",
                "make_zero",
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                temporary,
            ]);
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                arguments,
                s_timeout,
                token,
                workingDirectory: sessionFolder
            );
            if (!result.Succeeded)
            {
                throw new IOException($"Failed To Write The Clip, {result.LastError}");
            }

            var media = await MediaProbe.ProbeAsync(temporary, token);
            if (media is not { HasVideo: true, Duration: > 0.2 })
            {
                throw new InvalidDataException("The saved clip did not read back");
            }

            // The Logical Clip Starts At The Asked Moment Or Frame
            var keepStart = Math.Clamp(
                Math.Max(keepFrom, firstVideo.VideoStart) - fileStart,
                0,
                media.Duration
            );
            var keepEnd = Math.Clamp(keepTo - fileStart, keepStart + 0.1, media.Duration);
            var finalPath = UniquePath(clipsFolder, ClipNames.For(recordedAt));
            File.Move(temporary, finalPath);

            ClipLibrary.Register(
                finalPath,
                new ClipMetadata(
                    recordedAt,
                    monitorName,
                    media.Duration,
                    keepStart,
                    keepEnd,
                    null,
                    null,
                    media.HasAudio,
                    media.Width,
                    media.Height,
                    media.FrameRate,
                    0,
                    default
                )
            );
            await ClipLibrary.EnsureThumbnailAsync(
                finalPath,
                keepStart + Math.Min(1, (keepEnd - keepStart) / 2),
                token
            );
            return await library.GetAsync(finalPath, token)
                ?? throw new InvalidDataException("The saved clip did not read back");
        }
        finally
        {
            // Only This Save's Own Unfinished File, Never Anything Else
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static string UniquePath(string folder, string baseName)
    {
        // Never Overwrites, The Same Second Gets (2), (3) And On
        var path = Path.Combine(folder, baseName + ".mp4");
        for (var copy = 2; File.Exists(path); copy++)
        {
            path = Path.Combine(
                folder,
                string.Create(CultureInfo.InvariantCulture, $"{baseName} ({copy}).mp4")
            );
        }

        return path;
    }

    private static string ConcatOf(IReadOnlyList<Segment> segments)
    {
        return "concat:"
            + string.Join("|", segments.Select(segment => Path.GetFileName(segment.Path)));
    }

    private static string Seconds(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
