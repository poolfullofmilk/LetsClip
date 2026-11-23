using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LetsClip.Data.Helpers;
using NLog;

namespace LetsClip.Services.Integration;

public static partial class DiscordPost
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // An Upload Up To A Server's Limit Takes Its Time
    private static readonly HttpClient s_client = new() { Timeout = TimeSpan.FromMinutes(10) };

    static DiscordPost()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static string? WebhookOf(string? text)
    {
        // Only Discord's Own Webhook Links, Nothing Else Is Sent Anything
        var trimmed = text?.Trim() ?? "";
        return WebhookLink().IsMatch(trimmed) ? trimmed : null;
    }

    public static async Task<string?> NameOfAsync(string webhook, CancellationToken token)
    {
        // The Webhook's Own Name, Null When Discord Refuses It
        using var request = new HttpRequestMessage(HttpMethod.Get, webhook);
        request.Headers.UserAgent.ParseAdd(AppInfo.DisplayName);
        using var response = await s_client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            s_logger.Warn($"Failed To Connect A Discord Webhook, {(int)response.StatusCode}");
            return null;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return json.RootElement.TryGetProperty("name", out var name)
            ? name.GetString() ?? "Discord"
            : "Discord";
    }

    public static async Task<string?> PostAsync(
        string webhook,
        string file,
        string message,
        CancellationToken token
    )
    {
        // The File, Its Name The Message, Null When Posted
        try
        {
            await using var stream = File.OpenRead(file);
            using var content = new MultipartFormDataContent();
            content.Add(
                new StringContent(
                    JsonSerializer.Serialize(new { content = message }),
                    Encoding.UTF8,
                    "application/json"
                ),
                "payload_json"
            );
            var attached = new StreamContent(stream);
            attached.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(attached, "files[0]", Path.GetFileName(file));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{webhook}?wait=true")
            {
                Content = content,
            };
            request.Headers.UserAgent.ParseAdd(AppInfo.DisplayName);
            using var response = await s_client.SendAsync(request, token);
            s_logger.Info(
                $"Posted {Path.GetFileName(file)} To Discord, {(int)response.StatusCode}, {Formatter.FormatSize(stream.Length)}"
            );
            return response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => null,
                HttpStatusCode.RequestEntityTooLarge =>
                    "Too big for this Discord server, export it for a smaller plan",
                HttpStatusCode.NotFound or HttpStatusCode.Unauthorized =>
                    "The Discord channel's link stopped working, connect it again in Settings",
                HttpStatusCode.TooManyRequests => "Discord asked to wait, try again in a moment",
                _ => "Discord did not take the clip, try again",
            };
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            // Offline Or Slow, Nothing Was Posted
            s_logger.Warn($"Failed To Post To Discord {exception.GetType().Name}");
            return "Discord could not be reached, try again";
        }
    }

    // Discord's Own Hosts, A Numeric Id And A Token
    [GeneratedRegex(
        @"^https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api/webhooks/\d{5,30}/[A-Za-z0-9_\-]{20,120}$"
    )]
    private static partial Regex WebhookLink();

    private static bool SelfTestPasses()
    {
        return WebhookOf(
                " https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_-1234 "
            )
                is not null
            && WebhookOf(
                "https://ptb.discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz"
            )
                is not null
            && WebhookOf(
                "https://discord.com.evil.example/api/webhooks/123456789012345678/abcdefghijklmnopqrstuv"
            )
                is null
            && WebhookOf(
                "http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz"
            )
                is null
            && WebhookOf("https://discord.com/api/webhooks/123/abc") is null
            && WebhookOf(null) is null;
    }
}
