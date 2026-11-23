using System.Net.Http;
using System.Text.Json;
using LetsClip.Services.Capture;
using LetsClip.Services.Integration;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class DiscordChannel
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private bool _open;
    private string? _link;

    // Asking Discord, The Button Waits
    private bool Connecting { get; set; }

    // A Discord Shaped Link, Never Checked Twice At Once
    private bool CanConnect => !Connecting && DiscordPost.WebhookOf(_link) is not null;

    private void Open()
    {
        (_link, _open) = (null, true);
    }

    private void Close()
    {
        _open = false;
    }

    private async Task ConnectAsync()
    {
        // Discord Names The Webhook, Proving The Link Works
        if (DiscordPost.WebhookOf(_link) is not { } webhook)
        {
            return;
        }

        Connecting = true;
        string? name = null;
        try
        {
            name = await Task.Run(() => DiscordPost.NameOfAsync(webhook, CancellationToken.None));
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline Or Refused, Said Below
        }
        finally
        {
            Connecting = false;
        }

        if (name is null)
        {
            Snackbar.Add("This link did not work, copy it again from Discord", Severity.Warning);
            return;
        }

        Controller.UpdateSettings(settings =>
            settings with
            {
                DiscordWebhook = webhook,
                DiscordChannel = name,
            }
        );
        _open = false;
        Snackbar.Add("Connected, exports can now be posted there", Severity.Success);
    }

    private void Disconnect()
    {
        Controller.UpdateSettings(settings =>
            settings with
            {
                DiscordWebhook = null,
                DiscordChannel = null,
            }
        );
    }
}
