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

    // The Cut Is Being Written, Its Button Spins
    [Parameter]
    public bool Cutting { get; set; }

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

    // A Copy's Packets, Weighed Once Per Range And Sound
    private (TrimRange Range, bool WithAudio, long Bytes)? _copy;

    private long CopyBytes
    {
        get
        {
            if (_copy is not { } copy || copy.Range != Range || copy.WithAudio != WithAudio)
            {
                var bytes = Media.Packets is { } packets
                    ? SizeBudget.CopyBytes(packets, Range, WithAudio)
                    : (long)(Clip.SizeBytes * Range.Length / Media.Duration);
                _copy = copy = (Range, WithAudio, bytes);
            }

            return copy.Bytes;
        }
    }

    private bool IsExporting => _progress is not null;

    private bool IsCut => Range.CutsShort(Media.Duration);

    // Once Exported, The Same Selection Reads Again
    private string ExportText =>
        IsCut ? "Export and Save"
        : _result is null ? "Export"
        : "Export Again";

    private bool CanExport =>
        !IsExporting && !Busy && Range.IsValidFor(Media.Duration) && !IsTooLong;

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
            : Formatter.FormatSize(CopyBytes);

    // The Sound Rate The Page Script Adds While Dragging
    private string? CopyAudioText =>
        Budget is not null ? null
        : WithAudio && Media.Packets is { } packets
            ? SizeBudget
                .SoundBytesPerSecond(packets)
                .ToString("0.###", CultureInfo.InvariantCulture)
        : "0";

    // A Copy's Own Rate, An Encode's Ceiling Rounded Up
    private string BitrateText =>
        Budget is not { } budget ? Formatter.FormatBitrate(CopyBytes * 8 / Range.Length)
        : budget.VideoBitsPerSecond > 0
            ? Formatter.FormatBitrate(
                budget.VideoBitsPerSecond + (WithAudio ? SizeBudget.AudioBitsPerSecond : 0),
                roundUp: true
            )
        : "-";

    private bool IsTooLong => Budget?.Verdict == BudgetVerdict.TooSmall;

    // Names The Longest Selection The Plan Fits
    private string TooLongText =>
        $"Too long for {PlanLabel(Plan)}, which fits about {Formatter.FormatDuration(SizeBudget.LongestSeconds(SizeBudget.LimitOf(Plan) ?? 0, Shape))}, pick a shorter selection";

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
        return SizeBudget.LimitOf(plan) is not { } limit || CopyBytes > limit * 1_000_000;
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
            var request = new ExportRequest(
                Clip.Path,
                Range,
                Plan,
                FrameRate,
                WithAudio,
                Controller.Settings.ExportsDirectory
            );
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
            await JavaScript.CallAsync("letsClip.reveal", "exported");
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
            DesktopShell.CopyFile(result, Snackbar);
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
