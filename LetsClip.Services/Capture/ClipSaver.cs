using System.Globalization;
using System.IO.Pipes;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Media;
using NLog;

namespace LetsClip.Services.Capture;

public static class ClipSaver
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // A Scanner Holding The File Lets Go In A Moment
    private const int RemuxAttempts = 2;
    private static readonly TimeSpan s_retryWait = TimeSpan.FromSeconds(1);

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
            // Tried Again From The Same Segments, Still In The Cache
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await RemuxAsync(
                        video,
                        audio,
                        from,
                        to,
                        videoOffset,
                        separateTracks,
                        needed,
                        temporary
                    );
                    break;
                }
                catch (Exception exception)
                    when (attempt < RemuxAttempts && exception is IOException or TimeoutException)
                {
                    s_logger.Info($"Saving Again After {exception.GetType().Name}");
                    await Task.Delay(s_retryWait);
                }
            }

            // A Blocked Rename Waits, Never A Second Remux
            var finalPath = UniquePath(
                clipsFolder,
                ClipNames.For(ClipNames.SourceOf(game, monitor), recordedAt)
            );
            await ClipLibrary.ReplaceWhileBusyAsync(() => File.Move(temporary, finalPath));
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

    private static async Task RemuxAsync(
        IReadOnlyList<Segment> video,
        IReadOnlyList<Segment> audio,
        double from,
        double to,
        double videoOffset,
        bool separateTracks,
        long needed,
        string temporary
    )
    {
        // Each Stream Reads Through A Pipe, No Line Lists Segments
        var name = $"LetsClip-{Guid.NewGuid():N}";
        using var feeding = new CancellationTokenSource();
        List<Task> feeders = [FeedAsync($"{name}-video", video, feeding.Token)];
        IReadOnlyList<string> audioInput = ["-map", "0:v"];
        if (audio.Count > 0)
        {
            feeders.Add(FeedAsync($"{name}-audio", audio, feeding.Token));
            audioInput =
            [
                "-itsoffset",
                FFmpeg.Seconds(-from),
                "-f",
                "mpegts",
                "-i",
                $"//./pipe/{name}-audio",
                "-map",
                "0:v",
                "-map",
                "1:a",
            ];
        }

        try
        {
            // Stream Copy To The Press, The Replay Start Becomes Zero
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
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
                    "-f",
                    "mpegts",
                    "-i",
                    $"//./pipe/{name}-video",
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
                ],
                FFmpeg.CopyLimit(needed),
                CancellationToken.None
            );

            // The Remux Reports Its Own End, No Second Read
            if (!result.Succeeded || !WroteVideo(result.Output))
            {
                throw new IOException($"Failed To Write The Clip, {result.LastError}");
            }
        }
        finally
        {
            await feeding.CancelAsync();
            await Task.WhenAll(feeders);
        }
    }

    private static async Task FeedAsync(
        string pipeName,
        IReadOnlyList<Segment> segments,
        CancellationToken token
    )
    {
        // Segments In Order, From Memory Or Their Spilled Files
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
            0,
            1 << 20
        );
        try
        {
            await pipe.WaitForConnectionAsync(token);
            foreach (var segment in segments)
            {
                await segment.CopyToAsync(pipe, token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // FFmpeg Stopped Reading Early, Its Own Result Says Why
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

    public static string UniquePath(string folder, string baseName, string extension = ".mp4")
    {
        // Never Overwrites, The Same Second Gets (2), (3) And On
        var path = Path.Combine(folder, baseName + extension);
        for (var copy = 2; File.Exists(path); copy++)
        {
            path = Path.Combine(
                folder,
                string.Create(CultureInfo.InvariantCulture, $"{baseName} ({copy}){extension}")
            );
        }

        return path;
    }
}
