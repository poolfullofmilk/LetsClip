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
    public required string SourcePath { get; set; }

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
    private bool _includeAudio = true;

    // The Picked Size, Shown Within What The Selection Allows
    private double Megabytes
    {
        get => Math.Clamp(field, SizeBudget.MinimumMegabytes, MaximumMegabytes);
        set;
    }

    private double MaximumMegabytes =>
        SizeBudget.CeilingMegabytes(Media.SizeBytes, Range.Length, Media.Duration);

    private bool Use720p => Controller.Settings.Export720p && CanShrink;

    // A Clip At 720p Or Below Has Nothing Smaller
    private bool CanShrink => Math.Min(Media.Width, Media.Height) > 720;

    private bool IsExporting => _progress is not null;

    private bool CanExport =>
        !IsExporting
        && Range.IsValidFor(Media.Duration)
        && Budget.Verdict != BudgetVerdict.TooSmall;

    private (int Width, int Height) OutputSize =>
        ClipExporter.OutputSize(Media.Width, Media.Height, Use720p);

    private string OriginalLabel
    {
        get
        {
            var (width, height) = ClipExporter.OutputSize(Media.Width, Media.Height, false);
            return $"{Math.Min(width, height)}p";
        }
    }

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
                _includeAudio && Media.HasAudio,
                width,
                height,
                FrameRate
            );
        }
    }

    private string BudgetText =>
        Budget.VideoBitsPerSecond > 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"About {Budget.VideoBitsPerSecond / 1e6:0.0} Mbps for Video"
            )
            : "No room left for video";

    private string VerdictText =>
        Budget.Verdict switch
        {
            BudgetVerdict.Good => "Sharp",
            BudgetVerdict.Fair => "Good",
            BudgetVerdict.Low => "Soft",
            _ => "Too Small",
        };

    private Color VerdictColor =>
        Budget.Verdict switch
        {
            BudgetVerdict.Good => Color.Success,
            BudgetVerdict.Fair => Color.Info,
            BudgetVerdict.Low => Color.Warning,
            _ => Color.Error,
        };

    private string? VerdictHint =>
        Budget.Verdict switch
        {
            BudgetVerdict.Low when !Use720p && CanShrink =>
                "Fast motion will look soft at this size, 720p or a shorter selection keeps it sharper",
            BudgetVerdict.Low =>
                "Fast motion will look soft at this size, a shorter selection keeps it sharper",
            BudgetVerdict.TooSmall => ClipExporter.TooSmallMessage(Request),
            _ => null,
        };

    private string VerdictHintClass =>
        Budget.Verdict == BudgetVerdict.TooSmall ? "mud-error-text" : "mud-text-secondary";

    private string ExportLabel =>
        $"Export {Formatter.FormatSeconds(Range.Length)} Under {Formatter.FormatMegabytes(Megabytes)}";

    private ExportRequest Request =>
        new(
            SourcePath,
            Range,
            Megabytes,
            Use720p,
            Controller.Settings.ExportFullFrameRate,
            _includeAudio && Media.HasAudio,
            Controller.Settings.ExportsDirectory
        );

    protected override void OnInitialized()
    {
        Megabytes = Controller.Settings.ExportMegabytes;
    }

    private void SetFullFrameRate(bool full)
    {
        Controller.UpdateSettings(settings => settings with { ExportFullFrameRate = full });
    }

    private void SetResolution(bool use720p)
    {
        Controller.UpdateSettings(settings => settings with { Export720p = use720p });
    }

    private void SetAudio(bool include)
    {
        _includeAudio = include;
    }

    private async Task ExportAsync()
    {
        // The Size Is Saved On Export, Not Per Slider Step
        var megabytes = Megabytes;
        if (megabytes != Controller.Settings.ExportMegabytes)
        {
            Controller.UpdateSettings(settings => settings with { ExportMegabytes = megabytes });
        }

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
                    ? "Copied, paste it into Discord with Ctrl+V"
                    : "The clipboard is busy, try again",
                copied ? Severity.Success : Severity.Warning
            );
        }
    }

    private static string ResultText(ExportResult result)
    {
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{Formatter.FormatSize(result.SizeBytes)} · {Formatter.FormatSeconds(result.Duration)} · {result.Width} × {result.Height} at {result.FrameRate:0} FPS{(result.HasAudio ? "" : " · No Audio")}"
        );
    }

    public void Dispose()
    {
        // Leaving The Editor Ends A Running Export And Its File
        _cancel?.Cancel();
        GC.SuppressFinalize(this);
    }
}
