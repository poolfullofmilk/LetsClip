using System.Globalization;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
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

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private CancellationTokenSource? _cancel;
    private ExportProgress? _progress;
    private ExportResult? _result;
    private string? _error;
    private bool IncludeAudio { get; set; } = true;

    // Limits This Selection Would Feel, Then Unlimited
    private IReadOnlyList<double> Limits
    {
        get
        {
            var (width, height) = OutputSize;
            return SizeBudget.LimitsFor(
                Range.Length,
                width,
                height,
                FrameRate,
                WithAudio,
                SourceBytesPerSecond
            );
        }
    }

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
    private double Ceiling
    {
        get
        {
            var (width, height) = OutputSize;
            return SizeBudget.CeilingMegabytes(
                Range.Length,
                width,
                height,
                FrameRate,
                WithAudio,
                SourceBytesPerSecond
            );
        }
    }

    private double Megabytes => Math.Min(Limit, Ceiling);

    private bool WithAudio => IncludeAudio && Media.HasAudio;

    private double SourceBytesPerSecond => Clip.SizeBytes / Media.Duration;

    private bool IsExporting => _progress is not null;

    private bool CanExport =>
        !IsExporting
        && Range.IsValidFor(Media.Duration)
        && Budget.Verdict != BudgetVerdict.TooSmall;

    private (int Width, int Height) OutputSize =>
        ClipExporter.OutputSize(Media.Width, Media.Height);

    private double SourceFrameRate => ClipExporter.FrameRateOf(Media.FrameRate, true);

    private double FrameRate =>
        ClipExporter.FrameRateOf(Media.FrameRate, Controller.Settings.ExportFullFrameRate);

    private ExportBudget Budget
    {
        get
        {
            var (width, height) = OutputSize;
            return SizeBudget.Plan(
                Megabytes,
                Range.Length,
                WithAudio,
                width,
                height,
                FrameRate,
                SourceBytesPerSecond
            );
        }
    }

    // A Ceiling, Not A Promise, Still Scenes Come Out Smaller
    private string BudgetText =>
        Budget.VideoBitsPerSecond > 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Up to {Budget.VideoBitsPerSecond / 1e6:0.0} Mbps for Video"
            )
            : "No room left for video";

    private string VerdictText =>
        Budget.Verdict == BudgetVerdict.TooSmall ? "Too Small" : Budget.Verdict.ToString();

    private Color VerdictColor =>
        Budget.Verdict switch
        {
            BudgetVerdict.Sharp => Color.Success,
            BudgetVerdict.Good => Color.Info,
            BudgetVerdict.Soft => Color.Warning,
            _ => Color.Error,
        };

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

    private string ExportLabel =>
        Limit < SizeBudget.MaximumMegabytes
            ? $"Export {Formatter.FormatDuration(Range.Length)} Under {LimitLabel(Limit)}"
            : $"Export {Formatter.FormatDuration(Range.Length)}";

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
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{ClipExporter.FrameRateOf(Media.FrameRate, full):0} FPS"
        );
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
                () => Controller.Exporter.ExportAsync(request, progress, token),
                token
            );
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
            var copied = DesktopShell.CopyFile(result.Path);
            Snackbar.Add(
                copied
                    ? "Copied, paste it anywhere with Ctrl+V"
                    : "The clipboard is busy, try again",
                copied ? Severity.Success : Severity.Warning
            );
        }
    }

    private static string ResultText(ExportResult result)
    {
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{Formatter.FormatSize(result.SizeBytes)} · {Formatter.FormatDuration(result.Duration)} · {result.Width} × {result.Height} at {result.FrameRate:0} FPS{(result.HasAudio ? "" : " · No Audio")}"
        );
    }

    public void Dispose()
    {
        // Leaving The Editor Ends A Running Export And Its File
        _cancel?.Cancel();
        GC.SuppressFinalize(this);
    }
}
