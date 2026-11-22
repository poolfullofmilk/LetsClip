using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using NLog;

namespace LetsClip.Services.Media;

public static class MediaProbe
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);

    // A Listing Reads The Whole File, Big Ones Get Longer
    private const double ListingBytesPerSecond = 100_000_000;
    private static readonly TimeSpan s_longestListing = TimeSpan.FromSeconds(60);

    // Clips Whose Listing Ran Out, Only Their First Open Waits
    private static readonly ConcurrentDictionary<
        (string Path, long Size, DateTime Written),
        byte
    > s_unlisted = new();

    // Just Above A Black Capture's Brightest Grey, Either Range
    private const byte FirstLitLevel = 25;

    // ffprobe Uses Snake Case And Writes Numbers As Strings
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    static MediaProbe()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static async Task<MediaInfo?> ProbeAsync(
        string path,
        CancellationToken token,
        bool withPackets = false
    )
    {
        var result = await FFmpeg.RunAsync(
            FFmpeg.FFprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            s_timeout,
            token
        );
        if (!result.Succeeded)
        {
            return null;
        }

        Probed? probed;
        try
        {
            probed = Parse(result.Output);
        }
        catch (JsonException)
        {
            // A Damaged File Reads As Unreadable, Never As A Crash
            return null;
        }

        // Packets Come From Their Own Run, Which May Fail Alone
        return withPackets && probed is not null
            ? probed.Media with
            {
                Packets = await ListPacketsAsync(path, probed, token),
            }
            : probed?.Media;
    }

    private static async Task<PacketMap?> ListPacketsAsync(
        string path,
        Probed probed,
        CancellationToken token
    )
    {
        var file = new FileInfo(path);
        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        if (s_unlisted.ContainsKey(key))
        {
            return null;
        }

        // One Line A Packet, Read Without Decoding, Never Held Whole
        List<(double Time, long Size, bool Key)> frames = [];
        long audioBytes = 0;
        var audioPackets = 0;
        var limit = s_timeout + TimeSpan.FromSeconds(file.Length / ListingBytesPerSecond);
        try
        {
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFprobePath,
                [
                    "-v",
                    "error",
                    "-of",
                    "csv=p=0",
                    "-show_entries",
                    "packet=stream_index,pts_time,size,flags",
                    path,
                ],
                limit < s_longestListing ? limit : s_longestListing,
                token,
                outputLine: line =>
                {
                    if (ReadPacket(line) is not { } packet)
                    {
                        return;
                    }

                    if (packet.Stream == probed.VideoIndex)
                    {
                        frames.Add((packet.Time, packet.Size, packet.Key));
                    }
                    else if (packet.Stream == probed.AudioIndex && packet.Time >= 0)
                    {
                        // Sound Before Zero Is Hidden Pre-Roll, Not The Clip
                        audioBytes += packet.Size;
                        audioPackets++;
                    }
                }
            );
            if (result.Succeeded)
            {
                return MapOf(frames, audioBytes, audioPackets, probed.Seconds);
            }

            s_logger.Warn($"Failed To List The Packets Of {file.Name}, {result.LastError}");
        }
        catch (TimeoutException)
        {
            // The Clip Still Opens With Estimated Sizes, Not Listed Again
            s_unlisted[key] = 0;
            s_logger.Warn(
                $"Failed To List The Packets Of {file.Name} In Time, Sizes Are Estimated"
            );
        }

        return null;
    }

    private static (int Stream, double Time, long Size, bool Key)? ReadPacket(string line)
    {
        // Index, Time, Size, Flags, Then Whatever Side Data Adds
        var fields = line.Split(',');
        return
            fields.Length >= 4
            && int.TryParse(fields[0], CultureInfo.InvariantCulture, out var stream)
            && long.TryParse(fields[2], CultureInfo.InvariantCulture, out var size)
            ? (
                stream,
                double.TryParse(fields[1], CultureInfo.InvariantCulture, out var time)
                    ? time
                    : double.NaN,
                size,
                fields[3].Contains('K', StringComparison.Ordinal)
            )
            : null;
    }

    public static async Task<bool> IsBlackAsync(string path, CancellationToken token)
    {
        // Written Beside The Thumbnails, The Filter Takes A Plain Name
        Directory.CreateDirectory(AppInfo.ThumbnailsDirectory);
        var name = $"black-{Guid.NewGuid():N}.tmp";
        var sample = Path.Combine(AppInfo.ThumbnailsDirectory, name);
        try
        {
            var result = await FFmpeg.RunAsync(
                FFmpeg.FFmpegPath,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-loglevel",
                    "error",
                    "-y",
                    "-sseof",
                    "-10",
                    "-skip_frame",
                    "nokey",
                    "-i",
                    path,
                    "-an",
                    "-frames:v",
                    "10",
                    "-vf",
                    $"blackframe=amount=0:threshold={FirstLitLevel},metadata=print:key=lavfi.blackframe.pblack:file={name}",
                    "-f",
                    "null",
                    "-",
                ],
                s_timeout,
                token,
                workingDirectory: AppInfo.ThumbnailsDirectory
            );
            // Each Keyframe's Black Share, One Lit Pixel Reads 99
            var shares =
                result.Succeeded && File.Exists(sample)
                    ? (await File.ReadAllLinesAsync(sample, token))
                        .Where(line =>
                            line.StartsWith("lavfi.blackframe.pblack=", StringComparison.Ordinal)
                        )
                        .Select(line => line[(line.IndexOf('=') + 1)..])
                        .ToList()
                    : [];
            return shares.Count > 0 && shares.All(share => share == "100");
        }
        finally
        {
            File.Delete(sample);
        }
    }

    private static Probed? Parse(string json)
    {
        if (JsonSerializer.Deserialize<Probe>(json, s_json) is not { Format: { } format } probe)
        {
            return null;
        }

        // The First Real Picture Stream, Never A Cover Image
        var streams = probe.Streams ?? [];
        var video = streams.FirstOrDefault(stream =>
            stream.CodecType == "video" && stream.Disposition?.AttachedPic != 1
        );
        var audio = streams.FirstOrDefault(stream => stream.CodecType == "audio");
        var tracks = streams.Where(stream => stream.CodecType == "audio").ToList();

        // The Recorded Rate, A Game's Fewer Frames Never Lower It
        var nominal = RateOf(video?.RFrameRate);
        var frameRate =
            nominal is > 1 and < 241 ? nominal
            : RateOf(video?.AvgFrameRate) is > 0 and var average ? average
            : nominal;

        // A Sound Tail Under A Step Never Lengthens The Clip
        var duration =
            video?.Duration is > 0 and var picture && format.Duration - picture < TrimRange.Step
                ? picture
                : format.Duration;
        return new Probed(
            new MediaInfo(
                duration,
                video?.CodecName,
                video?.PixFmt,
                video?.Width ?? 0,
                video?.Height ?? 0,
                frameRate,
                audio?.CodecName,
                null,
                tracks.Count,
                // LetsClip's Own Mix, Game And Microphone, Named In That Order
                tracks
                    .Select(track => track.Tags?.HandlerName)
                    .SequenceEqual(AudioTracks.Separate),
                audio?.Tags?.HandlerName
            ),
            video?.Index,
            audio?.Index,
            format.Duration
        );
    }

    private static PacketMap MapOf(
        List<(double Time, long Size, bool Key)> packets,
        long audioBytes,
        int audioPackets,
        double duration
    )
    {
        // Each Video Frame's Start, Size And Keyframe, In Showing Order
        var frames = packets
            .Where(frame => double.IsFinite(frame.Time))
            .OrderBy(frame => frame.Time)
            .ToArray();

        // The Sound Measured, Its Own Bytes And Packets Per Second
        var seconds = Math.Max(duration, 0.001);
        return new PacketMap(
            [.. frames.Select(frame => frame.Time)],
            [.. frames.Select(frame => frame.Size)],
            [.. Enumerable.Range(0, frames.Length).Where(index => frames[index].Key)],
            audioBytes / seconds,
            audioPackets / seconds
        );
    }

    private static double RateOf(string? rate)
    {
        // Zero When The Fraction Is Missing Or Unreadable
        return
            rate?.Split('/') is [var top, var bottom]
            && double.TryParse(top, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(bottom, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0
            && denominator > 0
            ? numerator / denominator
            : 0;
    }

    private static bool SelfTestPasses()
    {
        // A Keyframe, Side Data's Field, No Time, No Packet
        return ReadPacket("0,0.016667,420168,K__") == (0, 0.016667, 420168, true)
            && ReadPacket("1,-0.021333,464,_D_,") == (1, -0.021333, 464, false)
            && ReadPacket("2,N/A,12,K_") is { Stream: 2, Size: 12, Key: true } untimed
            && double.IsNaN(untimed.Time)
            && ReadPacket("") is null
            && ReadPacket("side_data,1,2,3") is null;
    }

    // The Clip As Read, And What Its Packet Listing Needs
    private sealed record Probed(MediaInfo Media, int? VideoIndex, int? AudioIndex, double Seconds);

    private sealed record Probe(ProbeFormat? Format, ProbeStream[]? Streams);

    private sealed record ProbeFormat(double Duration);

    private sealed record ProbeStream(
        int Index,
        string? CodecType,
        string? CodecName,
        string? PixFmt,
        int Width,
        int Height,
        double? Duration,
        string? AvgFrameRate,
        string? RFrameRate,
        ProbeDisposition? Disposition,
        ProbeTags? Tags
    );

    private sealed record ProbeTags(string? HandlerName);

    private sealed record ProbeDisposition(int AttachedPic);
}
