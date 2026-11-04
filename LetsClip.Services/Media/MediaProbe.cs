using System.Globalization;
using System.Text.Json;
using LetsClip.Data.ViewModels;

namespace LetsClip.Services.Media;

public static class MediaProbe
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);

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
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or FormatException)
        {
            // A Damaged File Reads As Unreadable, Never As A Crash
            return null;
        }
    }

    private static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var streams = root.TryGetProperty("streams", out var list) ? list : default;
        JsonElement? video = null;
        JsonElement? audio = null;
        if (streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = TextOf(stream, "codec_type");
                if (type == "video" && video is null && !IsAttachedPicture(stream))
                {
                    video = stream;
                }
                else if (type == "audio" && audio is null)
                {
                    audio = stream;
                }
            }
        }

        string? VideoText(string name) => video is { } stream ? TextOf(stream, name) : null;
        double VideoNumber(string name) => video is { } stream ? NumberOf(stream, name) : 0;
        double AudioNumber(string name) => audio is { } stream ? NumberOf(stream, name) : 0;

        return new MediaInfo(
            TextOf(format, "format_name") ?? "",
            NumberOf(format, "duration"),
            NumberOf(format, "start_time"),
            (long)NumberOf(format, "size"),
            VideoText("codec_name"),
            VideoText("pix_fmt"),
            (int)VideoNumber("width"),
            (int)VideoNumber("height"),
            video is { } rateStream ? FrameRateOf(rateStream) : 0,
            VideoNumber("start_time"),
            audio is { } audioStream ? TextOf(audioStream, "codec_name") : null,
            (int)AudioNumber("channels"),
            AudioNumber("start_time"),
            VideoText("color_primaries"),
            VideoText("color_transfer"),
            VideoText("color_space")
        );
    }

    private static bool IsAttachedPicture(JsonElement stream)
    {
        return stream.TryGetProperty("disposition", out var disposition)
            && disposition.TryGetProperty("attached_pic", out var attached)
            && attached.ValueKind == JsonValueKind.Number
            && attached.GetInt32() == 1;
    }

    private static double FrameRateOf(JsonElement stream)
    {
        // Average Rate First, Variable Rate Streams Report Their Real Pace
        foreach (var name in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            var parts = TextOf(stream, name)?.Split('/');
            if (
                parts is [var numerator, var denominator]
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

    private static string? TextOf(JsonElement element, string name)
    {
        return
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double NumberOf(JsonElement element, string name)
    {
        // ffprobe Writes Most Numbers As Strings
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return
            value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number
            )
            ? number
            : 0;
    }
}
