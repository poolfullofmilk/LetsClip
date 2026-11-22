using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class ClipCard
{
    private static readonly DiscordPlan[] s_plans =
    [
        DiscordPlan.Free,
        DiscordPlan.NitroBasic,
        DiscordPlan.Nitro,
    ];

    [Parameter]
    public required ClipInfo Clip { get; set; }

    // Saved Since LetsClip Was Last Hidden
    [Parameter]
    public bool IsNew { get; set; }

    [Parameter]
    public string? Query { get; set; }

    // The Time Alone Under A Day, The Date Too Elsewhere
    [Parameter]
    public required string Time { get; set; }

    [Parameter]
    public string? CardStyle { get; set; }

    [Parameter]
    public bool Exporting { get; set; }

    [Parameter]
    public bool MakingPicture { get; set; }

    // Video Off, Discord Exports Keep The Sound Alone
    [Parameter]
    public bool SoundOnly { get; set; }

    [Parameter]
    public EventCallback Open { get; set; }

    [Parameter]
    public EventCallback Rename { get; set; }

    [Parameter]
    public EventCallback Delete { get; set; }

    [Parameter]
    public EventCallback<DiscordPlan> CopyForDiscord { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private object? _drawn;

    // Built On First Use, Most Cards Never Open Their Menu
    private MudMenu? _menu;
    private bool _menuBuilt;
    private MouseEventArgs? _opening;

    private IEnumerable<(string Label, string Value)> Facts
    {
        get
        {
            // The Editor's Order, Old Metadata May Lack Some
            yield return ("Size", Formatter.FormatSize(Clip.SizeBytes));
            if (Clip.Width > 0)
            {
                yield return ("Resolution", Formatter.FormatResolution(Clip.Width, Clip.Height));
            }

            if (Clip.FrameRate > 0)
            {
                yield return ("Frame Rate", Formatter.FormatFrameRate(Clip.FrameRate));
            }

            yield return ("Bitrate", Formatter.FormatBitrate(Clip.SizeBytes * 8 / Clip.Duration));
        }
    }

    // Screen Readers Hear The New Chip Too
    private string Description =>
        (IsNew ? "New, " : "")
        + string.Join(", ", Facts.Select(fact => $"{fact.Label} {fact.Value}"));

    private bool Changes(DiscordPlan plan)
    {
        // A Fitting Plan Only Copies, Sound Alone Becomes Opus
        return SoundOnly || Clip.SizeBytes > SizeBudget.LimitOf(plan) * 1_000_000;
    }

    private object Shown =>
        (Clip, IsNew, Query, Time, CardStyle, Exporting, MakingPicture, SoundOnly, _menuBuilt);

    private void BuildMenu(MouseEventArgs click)
    {
        (_menuBuilt, _opening) = (true, click);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _drawn = Shown;
        }

        // The Click That Built The Menu Opens It
        if (_opening is { } click && _menu is not null)
        {
            _opening = null;
            await _menu.OpenMenuAsync(click);
        }
    }

    protected override bool ShouldRender()
    {
        // Only A Card Whose Own Values Changed Draws Again
        var shown = Shown;
        if (Equals(shown, _drawn))
        {
            return false;
        }

        _drawn = shown;
        return true;
    }
}
