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
    public required Func<string, TrimRange, Task<bool>> SaveCut { get; set; }

    // The Clip Is Being Replaced, Nothing May Read It
    [Parameter]
    public bool Busy { get; set; }

    // A Shorter Selection, Ready Player And No Export Running
    [Parameter]
    public bool CanSaveCut { get; set; }

    // The Cut Is Being Written, Its Button Spins
    [Parameter]
    public bool Cutting { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required IJSRuntime JavaScript { get; set; }

    private bool _revealResult;

    private ExportRunner Exports => Controller.Exports;

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
    private ExportShape Shape
    {
        get
        {
            // Budgeted At Full Size, The Budget Then Picks The Lines
            var (width, height) = ClipExporter.OutputSize(Media.Width, Media.Height);
            return new(Range.Length, width, height, FrameRate, WithAudio);
        }
    }

    // Separate Tracks Can Leave Out Game Or Voice
    private bool KeepsGame => Controller.Settings.ExportAudio && Media.HasAudio;

    private bool KeepsMicrophone => Controller.Settings.ExportMicrophone;

    private int? Track =>
        Media.HasSeparateTracks ? AudioTracks.IndexOf(KeepsGame, KeepsMicrophone)
        : KeepsGame ? 0
        : null;

    private bool WithAudio => Track is not null;

    // The Clip Itself Keeps Every Track, An Export One
    private int CopiedTracks =>
        OnlyCopies ? Media.AudioTrackCount
        : WithAudio ? 1
        : 0;

    private double ClipBitsPerSecond => Clip.SizeBytes * 8 / Media.Duration;

    // A Copy's Packets, Weighed Once Per Range And Sound
    private readonly Dictionary<int, long> _copies = [];
    private TrimRange? _copiedRange;

    // One Picked Track, What A Plan Weighs Against Its Limit
    private long CopyBytes => CopyBytesOf(WithAudio ? 1 : 0);

    private long CopyBytesOf(int tracks)
    {
        // Weighed From The Packets Once Per Range And Track Count
        if (_copiedRange != Range)
        {
            _copiedRange = Range;
            _copies.Clear();
        }

        if (!_copies.TryGetValue(tracks, out var bytes))
        {
            bytes = Media.Packets is { } packets
                ? SizeBudget.CopyBytes(packets, Range, tracks)
                : (long)(Clip.SizeBytes * Range.Length / Media.Duration);
            _copies[tracks] = bytes;
        }

        return bytes;
    }

    private bool IsExporting => Exports.IsExporting(Clip.Path);

    // One Export At A Time, Another Clip's Is Running
    private bool OtherExporting => Exports.Running is not null && !IsExporting;

    private ExportProgress? ShownProgress => IsExporting ? Exports.Progress : null;

    // This Clip's Newest Export, Kept Past Leaving The Editor
    private string? ExportedFile =>
        Exports.Last is { File: { } file } last
        && last.Request.SourcePath == Clip.Path
        && File.Exists(file)
            ? file
            : null;

    private bool IsCut => Range.CutsShort(Media.Duration);

    // Off With Its Sound Kept, An Export Just Copies
    private bool OnlyCopies =>
        KeepsClip && (Media.HasSeparateTracks ? Track == 0 : WithAudio == Media.HasAudio);

    // Once Exported, The Same Selection Reads Again
    private string ExportText =>
        OnlyCopies ? (IsCut ? "Save and Copy" : "Copy File")
        : IsCut ? "Export and Save"
        : ExportedFile is null ? "Export"
        : "Export Again";

    private bool CanExport =>
        OnlyCopies
            ? !Busy && (!IsCut || CanSaveCut)
            : Exports.Running is null && !Busy && Range.IsValidFor(Media.Duration) && !IsTooLong;

    private (int Width, int Height) OutputSize =>
        Budget is { } budget
            ? ClipExporter.OutputSize(Media.Width, Media.Height, budget.Lines)
            : (Media.Width, Media.Height);

    // Odd Probes Read As 60
    private double ClipFrameRate =>
        Media.FrameRate is > 1 and < 241 ? Math.Round(Media.FrameRate) : 60;

    // An Encode Stops At 60
    private double FrameRate => KeepsClip ? ClipFrameRate : Math.Min(ClipFrameRate, 60);

    // Null While The Export Is An Exact Copy
    private ExportBudget? Budget =>
        SizeBudget.LimitOf(Plan) is { } limit ? SizeBudget.Plan(limit, Shape) : null;

    // A Copy's Weight, Or Where An Encode Lands
    private long ExportBytes => Budget?.TypicalBytes ?? CopyBytesOf(CopiedTracks);

    // Too Long Exports Nothing, So The Rows Show The Clip
    private string ClipSizeText => Formatter.FormatSize(Clip.SizeBytes);

    private string ExportSizeText => IsTooLong ? ClipSizeText : Formatter.FormatSize(ExportBytes);

    private string ClipResolutionText => Formatter.FormatResolution(Media.Width, Media.Height);

    private string ExportResolutionText =>
        IsTooLong
            ? ClipResolutionText
            : Formatter.FormatResolution(OutputSize.Width, OutputSize.Height);

    private string ClipFrameRateText => Formatter.FormatFrameRate(ClipFrameRate);

    private string ExportFrameRateText =>
        IsTooLong ? ClipFrameRateText : Formatter.FormatFrameRate(FrameRate);

    private string ClipBitrateText => Formatter.FormatBitrate(ClipBitsPerSecond);

    // The Sound Rate The Page Script Adds While Dragging
    private string? CopyAudioText =>
        Budget is not null ? null
        : WithAudio && Media.Packets is { } packets
            ? (SizeBudget.SoundBytesPerSecond(packets) * CopiedTracks).ToString(
                "0.###",
                CultureInfo.InvariantCulture
            )
        : "0";

    // A Copy Keeps The Clip's Rate, An Encode Its Most
    private string BitrateText =>
        IsTooLong ? ClipBitrateText
        : Budget is null ? Formatter.FormatBitrate(CopyBitsPerSecond)
        : Formatter.FormatBitrate(ExportBytes * 8 / Range.Length);

    // The Whole Clip's Rate, Less Each Sound Track Left Out
    private double CopyBitsPerSecond =>
        ClipBitsPerSecond
        - ((Media.AudioTrackCount - CopiedTracks) * (Media.Packets?.AudioBytesPerSecond ?? 0) * 8);

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

    private void SetPlan(DiscordPlan plan)
    {
        Controller.UpdateSettings(settings => settings with { ExportDiscordPlan = plan });
    }

    private void ToggleAudio()
    {
        Controller.UpdateSettings(settings => settings with { ExportAudio = !KeepsGame });
    }

    private void ToggleMicrophone()
    {
        Controller.UpdateSettings(settings =>
            settings with
            {
                ExportMicrophone = !KeepsMicrophone,
            }
        );
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

    protected override void OnInitialized()
    {
        Exports.Changed += OnExportsChanged;
        Exports.Progressed += OnExportsChanged;
        Exports.Finished += OnExportFinished;
    }

    private void OnExportsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnExportFinished(ExportOutcome outcome)
    {
        _ = InvokeAsync(() =>
        {
            // A Short Window Scrolls The Side Panel To The Export
            _revealResult = outcome.File is not null && outcome.Request.SourcePath == Clip.Path;
            StateHasChanged();
        });
    }

    private async Task CopyAsync()
    {
        // A Cut Is Saved First, Then The Clip Is Copied
        var (path, range, title) = (Clip.Path, Range, ExportText);
        if (!IsCut || (await ConfirmCutAsync(title) && await SaveCut(path, range)))
        {
            DesktopShell.CopyFile(path, Snackbar);
        }
    }

    private async Task ExportAsync()
    {
        if (OnlyCopies)
        {
            await CopyAsync();
            return;
        }

        var cut = IsCut;
        if (cut && !await ConfirmCutAsync(ExportText))
        {
            return;
        }

        var request = new ExportRequest(
            Clip.Path,
            Range,
            Plan,
            FrameRate,
            Track,
            Controller.Settings.ExportsDirectory
        );

        // Runs On Past Leaving, The App Bar Shows It
        _ = Exports.RunAsync(request, cut, !Controller.Settings.DeleteOriginals);
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
        Exports.Cancel();
    }

    private void CopyFile()
    {
        if (ExportedFile is { } file)
        {
            DesktopShell.CopyFile(file, Snackbar);
        }
    }

    public void Dispose()
    {
        Exports.Changed -= OnExportsChanged;
        Exports.Progressed -= OnExportsChanged;
        Exports.Finished -= OnExportFinished;
        GC.SuppressFinalize(this);
    }
}
