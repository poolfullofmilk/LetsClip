using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class GameSettingsButton
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Parameter]
    public required AddedGame Game { get; set; }

    // The Popover Edits A Copy Until Saved
    private GameCapture _draft = new("");
    private bool _open;

    private GameCapture Saved => Controller.GameSettingsOf(Game.Key) ?? new GameCapture(Game.Key);

    // What This Game Records Differently, Nothing When It Follows Settings
    private string? Summary
    {
        get
        {
            var saved = Saved;
            string?[] parts =
            [
                saved.Resolution is { } lines ? Formatter.FormatLines(lines) : null,
                saved.FrameRate is { } rate ? Formatter.FormatFrameRate(rate) : null,
                saved.Quality?.ToString(),
            ];
            return saved.IsDefault ? null : string.Join(", ", parts.OfType<string>());
        }
    }

    private int? Resolution
    {
        get => _draft.Resolution;
        set => _draft = _draft with { Resolution = value };
    }

    private int? FrameRate
    {
        get => _draft.FrameRate;
        set => _draft = _draft with { FrameRate = value };
    }

    private CaptureQuality? Quality
    {
        get => _draft.Quality;
        set => _draft = _draft with { Quality = value };
    }

    private void Open()
    {
        _draft = Saved;
        _open = true;
    }

    private void Close()
    {
        _open = false;
    }

    private void Reset()
    {
        _draft = new GameCapture(Game.Key);
    }

    private void Save()
    {
        Controller.SetGameSettings(_draft);
        _open = false;
    }

    private static string ResolutionLabel(int? lines)
    {
        return lines is { } picked ? Formatter.FormatLines(picked) : "Default";
    }

    private static string FrameRateLabel(int? rate)
    {
        return rate is { } picked ? Formatter.FormatFrameRate(picked) : "Default";
    }

    private static string QualityLabel(CaptureQuality? quality)
    {
        return quality?.ToString() ?? "Default";
    }
}
