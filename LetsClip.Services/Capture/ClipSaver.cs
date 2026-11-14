using System.Globalization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Media;

namespace LetsClip.Services.Capture;

public static class ClipSaver
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    // Windows' Own Disk Full Code, Whoever Runs Out First
    public const int DiskFullCode = unchecked((int)0x80070070);
    private const long DiskMargin = 50_000_000;

    private static long FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception exception)
            when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Unknown Space Is Tried, FFmpeg Then Says If It Fails
            return long.MaxValue;
        }
    }

    public static async Task<ClipInfo> SaveAsync(
        IReadOnlyList<Segment> video,
        IReadOnlyList<Segment> audio,
        double from,
        double to,
        double videoOffset,
        string sessionFolder,
        DateTimeOffset recordedAt,
        int frameRate,
        string? game,
        int? monitor,
        bool separateTracks,
        ClipLibrary library
    )
    {
        var clipsFolder = FolderFor(library.Folder);

        // A Full Drive Is Named Before FFmpeg Fails Halfway
        var needed = video.Sum(segment => segment.Bytes) + audio.Sum(segment => segment.Bytes);
        if (FreeBytes(clipsFolder) < needed + DiskMargin)
        {
            throw new IOException("Not Enough Space For The Clip", DiskFullCode);
        }

        var temporary = ClipLibrary.PartialPathIn(clipsFolder);
        try
        {
            // Stream Copy To The Press, The Replay Start Becomes Zero
            IReadOnlyList<string> audioInput =
                audio.Count > 0
                    ?
                    [
                        "-itsoffset",
                        FFmpeg.Seconds(-from),
                        "-i",
                        ConcatOf(audio),
                        "-map",
                        "0:v",
                        "-map",
                        "1:a",
                    ]
                    : ["-map", "0:v"];
            List<string> arguments =
            [
                "-hide_banner",
                "-nostdin",
                "-loglevel",
                "error",
                "-y",
                "-copyts",
                "-itsoffset",
                FFmpeg.Seconds(videoOffset - from),
                "-i",
                ConcatOf(video),
                .. audioInput,
                "-c",
                "copy",
                .. separateTracks ? TrackNames() : [],
                "-to",
                FFmpeg.Seconds(to - from),
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                temporary,
            ];
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                arguments,
                s_timeout,
                CancellationToken.None,
                workingDirectory: sessionFolder
            );
            if (!result.Succeeded)
            {
                throw new IOException($"Failed To Write The Clip, {result.LastError}");
            }

            var media = await MediaProbe.ProbeAsync(temporary, CancellationToken.None);
            if (media is not { HasVideo: true, Duration: > 0.2 })
            {
                throw new InvalidDataException("The saved clip did not read back");
            }

            var finalPath = UniquePath(
                clipsFolder,
                ClipNames.For(ClipNames.SourceOf(game, monitor), recordedAt)
            );
            File.Move(temporary, finalPath);
            // Keeps The Capture Rate, Game And Monitor, Thumbnails Come Later
            ClipLibrary.WriteMetadata(
                finalPath,
                new ClipMetadata(
                    recordedAt,
                    media.Duration,
                    null,
                    null,
                    0,
                    default,
                    frameRate,
                    null,
                    game,
                    media.Width,
                    media.Height,
                    Monitor: monitor
                )
            );
            return await library.GetAsync(finalPath, CancellationToken.None)
                ?? throw new InvalidDataException("The saved clip did not read back");
        }
        finally
        {
            // Only This Save's Own Unfinished File, Never Anything Else
            await FFmpeg.DeleteOutputAsync(temporary);
        }
    }

    private static List<string> TrackNames()
    {
        // Named For Players, Only The Mix Plays By Default
        List<string> arguments = [];
        for (var index = 0; index < AudioTracks.Separate.Length; index++)
        {
            arguments.AddRange([
                $"-metadata:s:a:{index}",
                $"handler_name={AudioTracks.Separate[index]}",
                $"-disposition:a:{index}",
                index == 0 ? "default" : "0",
            ]);
        }

        return arguments;
    }

    private static string FolderFor(string picked)
    {
        // A Gone Or Locked Folder Saves To Videos
        if (ClipLibrary.CanWriteTo(picked))
        {
            return picked;
        }

        Directory.CreateDirectory(AppInfo.DefaultClipsDirectory);
        return AppInfo.DefaultClipsDirectory;
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
}
