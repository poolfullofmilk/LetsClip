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
    public EventCallback<TrimRange> OnSaveCut { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private CancellationTokenSource? _cancel;
    private ExportProgress? _progress;
    private string? _result;
    private string? _error;

    // Limits This Selection Would Feel, Then Unlimited
    private IReadOnlyList<double> Limits => SizeBudget.LimitsFor(Shape);

    // The Saved Limit, Or The Nearest Offered Below It
    private double Limit
    {
        get
        {
            var saved = Controller.Settings.ExportMegabytes;
            var limits = Limits;
            return saved >= Ceiling
                ? SizeBudget.MaximumMegabytes
                : limits.Where(limit => limit <= saved).DefaultIfEmpty(limits[0]).Max();
        }
    }

    // Past This More Size Adds Nothing, So Unlimited Aims Here
    private double Ceiling => SizeBudget.CeilingMegabytes(Shape);

    // The Selection As The Size Budget Reads It
    private ExportShape Shape =>
        new(
            Range.Length,
            OutputSize.Width,
            OutputSize.Height,
            FrameRate,
            WithAudio,
            SourceBytesPerSecond
        );

    private double Megabytes => Math.Min(Limit, Ceiling);

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
        && Range.IsValidFor(Media.Duration)
        && Budget.Verdict != BudgetVerdict.TooSmall;

    private (int Width, int Height) OutputSize =>
        ClipExporter.OutputSize(Media.Width, Media.Height);

    private double SourceFrameRate => ClipExporter.FrameRateOf(Media.FrameRate, true);

    private double FrameRate =>
        ClipExporter.FrameRateOf(Media.FrameRate, Controller.Settings.ExportFullFrameRate);

    private ExportBudget Budget => SizeBudget.Plan(Megabytes, Shape);

    // The Most Video Gets, Still Scenes Come Out Smaller
    private string BitrateText =>
        Budget.VideoBitsPerSecond > 0
            ? $"Up to {Formatter.FormatBitrate(Budget.VideoBitsPerSecond)}"
            : "-";

    private string? VerdictHint =>
        Budget.Verdict switch
        {
            BudgetVerdict.Soft =>
                "Fast motion will look soft at this size, a shorter selection keeps it sharper",
            BudgetVerdict.TooSmall => ClipExporter.TooSmallMessage,
            _ => null,
        };

    private string VerdictHintClass =>
        Budget.Verdict == BudgetVerdict.TooSmall ? "mud-error-text" : "mud-text-secondary";

    private ExportRequest Request =>
        new(
            Clip.Path,
            Range,
            Megabytes,
            FrameRate,
            WithAudio,
            Controller.Settings.ExportsDirectory
        );

    private static string LimitLabel(double megabytes)
    {
        return megabytes >= SizeBudget.MaximumMegabytes
            ? "Unlimited"
            : Formatter.FormatMegabytes(megabytes);
    }

    private void SetLimit(double megabytes)
    {
        // Saved As Picked, A Short Selection Never Lowers It
        Controller.UpdateSettings(settings => settings with { ExportMegabytes = megabytes });
    }

    private string FrameRateLabel(bool full)
    {
        return $"{ClipExporter.FrameRateOf(Media.FrameRate, full):0} FPS";
    }

    private void ToggleAudio()
    {
        Controller.UpdateSettings(settings => settings with { ExportAudio = !WithAudio });
    }

    private void SetFullFrameRate(bool full)
    {
        Controller.UpdateSettings(settings => settings with { ExportFullFrameRate = full });
    }

    private async Task ExportAsync()
    {
        _error = null;
        _result = null;
        _cancel = new CancellationTokenSource();
        _progress = new ExportProgress("Starting", 0);

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

            // The Exported Selection, Even If The Handles Moved Since
            if (request.Range.CutsShort(Media.Duration))
            {
                _progress = new ExportProgress("Saving the Cut", 1);
                StateHasChanged();
                await OnSaveCut.InvokeAsync(request.Range);
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
        _cancel?.Cancel();
        GC.SuppressFinalize(this);
    }
}
