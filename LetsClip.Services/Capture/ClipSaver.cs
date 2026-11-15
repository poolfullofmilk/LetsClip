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

    internal static long? FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception exception)
            when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Unknown, Each Caller Decides What That Means
            return null;
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
        (int Width, int Height) size,
        string? game,
        int? monitor,
        bool separateTracks,
        ClipLibrary library
    )
    {
        var clipsFolder = FolderFor(library.Folder);

        // Named Before FFmpeg Fails Halfway, Unknown Space Is Tried
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
                "-nostats",
                "-progress",
                "pipe:1",
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
            // The Remux Reports Its Own End, No Second Read
            if (!result.Succeeded || !WroteVideo(result.Output))
            {
                throw new IOException($"Failed To Write The Clip, {result.LastError}");
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
                    to - from,
                    null,
                    null,
                    0,
                    default,
                    frameRate,
                    null,
                    game,
                    size.Width,
                    size.Height,
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

    private static bool WroteVideo(string progress)
    {
        // Ended With Frames Written, Its Reported Times Mislead Here
        var frames = progress
            .Split('\n')
            .LastOrDefault(line => line.StartsWith("frame=", StringComparison.Ordinal));
        return progress.Contains("progress=end", StringComparison.Ordinal)
            && int.TryParse(
                frames?["frame=".Length..].Trim(),
                CultureInfo.InvariantCulture,
                out var count
            )
            && count > 0;
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
