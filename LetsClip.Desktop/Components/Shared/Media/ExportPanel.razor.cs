using System.Globalization;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Common;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using NLog;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class ExportPanel : IDisposable
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    [Parameter]
    public required ClipInfo Clip { get; set; }

    [Parameter]
    public required TrimRange Range { get; set; }

    [Parameter]
    public required MediaInfo Media { get; set; }

    // The Clip's Own Rows, Name And Recorded, Above The Export's
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // A Shorter Selection Is Also Cut Into The Clip
    [Parameter]
    public required Func<string, TrimRange, Task> SaveCut { get; set; }

    // The Clip Is Being Replaced, Nothing May Read It
    [Parameter]
    public bool Busy { get; set; }

    // A Shorter Selection, Ready Player And No Export Running
    [Parameter]
    public bool CanSaveCut { get; set; }

    [Parameter]
    public EventCallback<bool> ExportingChanged { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required IJSRuntime JavaScript { get; set; }

    private CancellationTokenSource? _cancel;
    private ExportProgress? _progress;
    private string? _result;
    private string? _error;
    private bool _disposed;
    private bool _revealResult;

    // The Saved Plan, Off While The Selection Already Fits
    private DiscordPlan Plan =>
        Changes(Controller.Settings.ExportDiscordPlan)
            ? Controller.Settings.ExportDiscordPlan
            : DiscordPlan.Off;

    // Off Copies Exactly, A Plan Only Past Its Limit
    private bool KeepsClip => Plan == DiscordPlan.Off;

    private bool AnyPlanChanges =>
        Enum.GetValues<DiscordPlan>().Any(plan => plan != DiscordPlan.Off && Changes(plan));

    // The Selection As The Size Budget Reads It
    private ExportShape Shape =>
        new(Range.Length, OutputSize.Width, OutputSize.Height, FrameRate, WithAudio);

    private bool WithAudio => Controller.Settings.ExportAudio && Media.HasAudio;

    private double ClipBitsPerSecond => Clip.SizeBytes * 8 / Media.Duration;

    // A Muted Copy Leaves The Sound's Bytes Behind
    private double SourceBytesPerSecond =>
        (ClipBitsPerSecond - (WithAudio ? 0 : Media.AudioBitsPerSecond)) / 8;

    private bool IsExporting => _progress is not null;

    private bool IsCut => Range.CutsShort(Media.Duration);

    // Once Exported, The Same Selection Reads Again
    private string ExportText =>
        IsCut ? "Export and Save"
        : _result is null ? "Export"
        : "Export Again";

    private bool CanExport =>
        !IsExporting
        && !Busy
        && Range.IsValidFor(Media.Duration)
        && Budget?.Verdict != BudgetVerdict.TooSmall;

    private (int Width, int Height) OutputSize =>
        KeepsClip
            ? (Media.Width, Media.Height)
            : ClipExporter.OutputSize(Media.Width, Media.Height);

    // Odd Probes Read As 60
    private double ClipFrameRate =>
        Media.FrameRate is > 1 and < 241 ? Math.Round(Media.FrameRate) : 60;

    // An Encode Stops At 60
    private double FrameRate => KeepsClip ? ClipFrameRate : Math.Min(ClipFrameRate, 60);

    // Null While The Export Is An Exact Copy
    private ExportBudget? Budget =>
        SizeBudget.LimitOf(Plan) is { } limit ? SizeBudget.Plan(limit, Shape) : null;

    // Copies Weigh Their Share, Encodes At Most Their Plan
    private string ExportSizeText =>
        SizeBudget.LimitOf(Plan) is { } limit
            ? Formatter.FormatSize((long)(limit * 1_000_000))
            : Formatter.FormatSize((long)Math.Round(SourceBytesPerSecond * Range.Length));

    // A Copy's Share Follows Drags, About Since Keyframes Lead In
    private string? CopyBytesPerSecond =>
        Budget is null
            ? SourceBytesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)
            : null;

    // The Whole File's Rate, Never Raised By Muting
    private string BitrateText =>
        Budget is not { } budget ? Formatter.FormatBitrate(SourceBytesPerSecond * 8)
        : budget.VideoBitsPerSecond > 0
            ? Formatter.FormatBitrate(
                budget.VideoBitsPerSecond + (WithAudio ? SizeBudget.AudioBitsPerSecond : 0)
            )
        : "-";

    private string? VerdictHint =>
        Budget?.Verdict switch
        {
            BudgetVerdict.Soft =>
                "Fast motion will look soft at this size, a shorter selection keeps it sharper",
            BudgetVerdict.TooSmall => ClipExporter.TooSmallMessage,
            _ => null,
        };

    private string VerdictHintClass =>
        Budget?.Verdict == BudgetVerdict.TooSmall ? "mud-error-text" : "mud-text-secondary";

    private ExportRequest Request =>
        new(Clip.Path, Range, Plan, FrameRate, WithAudio, Controller.Settings.ExportsDirectory);

    private static string PlanLabel(DiscordPlan plan)
    {
        return plan switch
        {
            DiscordPlan.Free => "Free",
            DiscordPlan.NitroBasic => "Nitro Basic",
            DiscordPlan.Nitro => "Nitro",
            _ => "Off",
        };
    }

    private static string? PlanDetail(DiscordPlan plan)
    {
        return SizeBudget.LimitOf(plan) is { } limit
            ? Formatter.FormatSize((long)(limit * 1_000_000))
            : null;
    }

    // A Plan The Selection Fits Would Only Copy, Like Off
    private bool Changes(DiscordPlan plan)
    {
        return plan == DiscordPlan.Off
            || !SizeBudget.KeepsClip(plan, Range.Length, SourceBytesPerSecond);
    }

    private static string SizeText(int width, int height)
    {
        return $"{width} × {height}";
    }

    private static string RateText(double rate)
    {
        return $"{rate:0} FPS";
    }

    private void SetPlan(DiscordPlan plan)
    {
        Controller.UpdateSettings(settings => settings with { ExportDiscordPlan = plan });
    }

    private void ToggleAudio()
    {
        Controller.UpdateSettings(settings => settings with { ExportAudio = !WithAudio });
    }

    private async Task SaveCutAsync()
    {
        var (path, range) = (Clip.Path, Range);
        if (await ConfirmCutAsync("Save Cut"))
        {
            await SaveCut(path, range);
        }
    }

    private async Task<bool> ConfirmCutAsync(string title)
    {
        return !Controller.Settings.DeleteOriginals
            || await YesNoDialog.ConfirmCutAsync(DialogService, title);
    }

    private async Task ExportAsync()
    {
        if (IsCut && !await ConfirmCutAsync(ExportText))
        {
            return;
        }

        _error = null;
        _result = null;
        _cancel = new CancellationTokenSource();
        _progress = new ExportProgress("Starting", 0);
        var duration = Media.Duration;
        await ExportingChanged.InvokeAsync(true);

        // Progress Lands On The UI Thread, Throttled To Whole Percents
        var progress = new Progress<ExportProgress>(report =>
        {
            if (_progress is not null)
            {
                _progress = report;
                StateHasChanged();
            }
        });
        try
        {
            var request = Request;
            var token = _cancel.Token;
            _result = await Task.Run(
                () => ClipExporter.ExportAsync(request, progress, token),
                token
            );

            // The Exported Selection, Only While Its Own Clip Is Open
            if (!_disposed && request.Range.CutsShort(duration))
            {
                _progress = new ExportProgress("Saving the Cut", 1);
                StateHasChanged();
                await SaveCut(request.SourcePath, request.Range);
            }
        }
        catch (OperationCanceledException)
        {
            _error = "Export cancelled, nothing was kept";
        }
        catch (Exception exception) when (exception is ExportException or TimeoutException)
        {
            _error = exception.Message;
            s_logger.Warn($"Failed To Export, {exception.Message}");
        }
        finally
        {
            // A Short Window Scrolls The Side Panel To The Export
            _revealResult = _result is not null;
            _progress = null;
            _cancel.Dispose();
            _cancel = null;
            if (!_disposed)
            {
                await ExportingChanged.InvokeAsync(false);
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_revealResult)
        {
            _revealResult = false;
            await JavaScript.InvokeVoidAsync("letsClip.reveal", "exported");
        }
    }

    private void Cancel()
    {
        _cancel?.Cancel();
    }

    private void CopyFile()
    {
        if (_result is { } result)
        {
            var copied = DesktopShell.CopyFile(result);
            Snackbar.Add(
                copied
                    ? "Copied, paste it anywhere with Ctrl+V"
                    : "The clipboard is busy, try again",
                copied ? Severity.Success : Severity.Warning
            );
        }
    }

    public void Dispose()
    {
        // Leaving The Editor Ends A Running Export And Its File
        _disposed = true;
        _cancel?.Cancel();
        GC.SuppressFinalize(this);
    }
}
