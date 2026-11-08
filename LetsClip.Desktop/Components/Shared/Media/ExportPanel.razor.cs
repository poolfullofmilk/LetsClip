using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Common;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
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

    // A Shorter Selection Is Also Cut Into The Clip
    [Parameter]
    public required Func<string, TrimRange, Task> SaveCut { get; set; }

    // The Clip Is Being Replaced, Nothing May Read It
    [Parameter]
    public bool Busy { get; set; }

    [Parameter]
    public EventCallback<bool> ExportingChanged { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private CancellationTokenSource? _cancel;
    private ExportProgress? _progress;
    private string? _result;
    private string? _error;
    private bool _disposed;

    private DiscordPlan Plan => Controller.Settings.ExportDiscordPlan;

    // Off, Or Already Small Enough, Is An Exact Copy
    private bool KeepsClip => SizeBudget.KeepsClip(Plan, Range.Length, SourceBytesPerSecond);

    // The Selection As The Size Budget Reads It
    private ExportShape Shape =>
        new(Range.Length, OutputSize.Width, OutputSize.Height, FrameRate, WithAudio);

    private bool WithAudio => Controller.Settings.ExportAudio && Media.HasAudio;

    private double SourceBytesPerSecond => Clip.SizeBytes / Media.Duration;

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

    // Odd Probes Read As 60, An Encode Stops At 60
    private double FrameRate
    {
        get
        {
            var rate = Media.FrameRate is > 1 and < 241 ? Math.Round(Media.FrameRate) : 60;
            return KeepsClip ? rate : Math.Min(rate, 60);
        }
    }

    // Null While The Export Is An Exact Copy
    private ExportBudget? Budget =>
        KeepsClip || SizeBudget.LimitOf(Plan) is not { } limit
            ? null
            : SizeBudget.Plan(limit, Shape);

    // The Clip's Own, Or The Most An Encode Gets
    private string BitrateText =>
        Budget is not { } budget ? Formatter.FormatBitrate(SourceBytesPerSecond * 8)
        : budget.VideoBitsPerSecond > 0
            ? $"Up to {Formatter.FormatBitrate(budget.VideoBitsPerSecond)}"
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
        return SizeBudget.LimitOf(plan) is { } limit ? Formatter.FormatMegabytes(limit) : null;
    }

    private void SetPlan(DiscordPlan plan)
    {
        Controller.UpdateSettings(settings => settings with { ExportDiscordPlan = plan });
    }

    private void ToggleAudio()
    {
        Controller.UpdateSettings(settings => settings with { ExportAudio = !WithAudio });
    }

    private async Task ExportAsync()
    {
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
            _progress = null;
            _cancel.Dispose();
            _cancel = null;
            if (!_disposed)
            {
                await ExportingChanged.InvokeAsync(false);
            }
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
