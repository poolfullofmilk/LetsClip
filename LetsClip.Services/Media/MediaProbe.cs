using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;

namespace LetsClip.Services.Media;

public static class MediaProbe
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);

    // ffprobe Uses Snake Case And Writes Numbers As Strings
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static async Task<MediaInfo?> ProbeAsync(string path, CancellationToken token)
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

        try
        {
            return Parse(result.Output);
        }
        catch (JsonException)
        {
            // A Damaged File Reads As Unreadable, Never As A Crash
            return null;
        }
    }

    public static async Task<double[]> FrameTimesAsync(string path, CancellationToken token)
    {
        // Each Video Frame's Start, Read From Packets Without Decoding
        var result = await FFmpeg.RunAsync(
            FFmpeg.FFprobePath,
            [
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "packet=pts_time",
                "-of",
                "csv=p=0",
                path,
            ],
            s_timeout,
            token
        );
        return result.Succeeded
            ?
            [
                .. result
                    .Output.Split('\n')
                    .Select(line =>
                        double.TryParse(
                            line.Trim().TrimEnd(','),
                            CultureInfo.InvariantCulture,
                            out var time
                        )
                            ? time
                            : double.NaN
                    )
                    .Where(double.IsFinite)
                    .Order(),
            ]
            : [];
    }

    private static MediaInfo? Parse(string json)
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

        // Average Rate First, Variable Rate Streams Report Their Real Pace
        var frameRate = RateOf(video?.AvgFrameRate) is > 0 and var average
            ? average
            : RateOf(video?.RFrameRate);

        // A Sound Tail Under A Step Never Lengthens The Clip
        var duration =
            video?.Duration is > 0 and var picture && format.Duration - picture < TrimRange.Step
                ? picture
                : format.Duration;
        return new MediaInfo(
            duration,
            video?.CodecName,
            video?.PixFmt,
            video?.Width ?? 0,
            video?.Height ?? 0,
            frameRate,
            audio?.CodecName
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

    private sealed record Probe(ProbeFormat? Format, ProbeStream[]? Streams);

    private sealed record ProbeFormat(double Duration);

    private sealed record ProbeStream(
        string? CodecType,
        string? CodecName,
        string? PixFmt,
        int Width,
        int Height,
        double? Duration,
        string? AvgFrameRate,
        string? RFrameRate,
        ProbeDisposition? Disposition
    );

    private sealed record ProbeDisposition(int AttachedPic);
}
