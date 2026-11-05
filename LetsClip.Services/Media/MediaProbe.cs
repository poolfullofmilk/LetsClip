using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        // A Missing Duration Reads As N/A, Taken As Zero
        return new MediaInfo(
            double.TryParse(
                format.Duration,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration
            )
                ? duration
                : 0,
            video?.CodecName,
            video?.PixFmt,
            video?.Width ?? 0,
            video?.Height ?? 0,
            video is null ? 0 : FrameRateOf(video),
            audio?.CodecName
        );
    }

    private static double FrameRateOf(ProbeStream stream)
    {
        // Average Rate First, Variable Rate Streams Report Their Real Pace
        foreach (var rate in new[] { stream.AvgFrameRate, stream.RFrameRate })
        {
            if (
                rate?.Split('/') is [var numerator, var denominator]
                && double.TryParse(
                    numerator,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var top
                )
                && double.TryParse(
                    denominator,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var bottom
                )
                && top > 0
                && bottom > 0
            )
            {
                return top / bottom;
            }
        }

        return 0;
    }

    private sealed record Probe(ProbeFormat? Format, ProbeStream[]? Streams);

    private sealed record ProbeFormat(string? Duration);

    private sealed record ProbeStream(
        string? CodecType,
        string? CodecName,
        string? PixFmt,
        int Width,
        int Height,
        string? AvgFrameRate,
        string? RFrameRate,
        ProbeDisposition? Disposition
    );

    private sealed record ProbeDisposition(int AttachedPic);
}
