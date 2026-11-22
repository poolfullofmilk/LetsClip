using System.ComponentModel;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using LetsClip.Services.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class ExportPanel : IDisposable
{
    [Parameter]
    public required ClipInfo Clip { get; set; }

    [Parameter]
    public required TrimRange KeptRange { get; set; }

    [Parameter]
    public required MediaInfo Media { get; set; }

    // The Clip's Own Rows, Name And Recorded, Above The Export's
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // A Shorter Selection Is Also Cut Into The Clip
    [Parameter]
    public required Func<string, TrimRange, Task<bool>> SaveCut { get; set; }

    // The Clip's File Once A Typed Name Reached It
    [Parameter]
    public required Func<Task<string?>> SettledPath { get; set; }

    // The Clip Is Being Replaced, Nothing May Read It
    [Parameter]
    public bool Busy { get; set; }

    // A Ready Player And No Export Running
    [Parameter]
    public bool CanCut { get; set; }

    // The Cut Is Being Written, Its Button Spins
    [Parameter]
    public bool Cutting { get; set; }

    // The Crop's Place And Size, Null Keeps The Whole Frame
    [Parameter]
    public CropBox? KeptCrop { get; set; }

    // Video, Game Audio Or Microphone Kept Or Left Out
    [Parameter]
    public EventCallback StreamsChanged { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    [Inject]
    public required IJSRuntime JavaScript { get; set; }

    private bool _revealResult;

    // Mid Drag The Page's Range Or Box, Drawn Here Alone
    private TrimRange? _liveRange;

    private TrimRange Range => _liveRange ?? KeptRange;

    private CropBox? Crop
    {
        get => field ?? KeptCrop;
        set;
    }

    private bool CanSaveCut => CanCut && Range.CutsShort(Media.Duration);

    // The Shown Export's Facts And Lost Sound, Read Once
    private string? _shownExport;
    private string _exportFacts = NoFacts;
    private (string Icon, string Text)? _leftOut;

    // Holds The Facts Line's Height Until They Are Read
    private const string NoFacts = " ";

    private ExportRunner Exports => Controller.Exports;

    // The Saved Plan, Off While The Selection Already Fits
    private DiscordPlan Plan =>
        Changes(Controller.Settings.ExportDiscordPlan)
            ? Controller.Settings.ExportDiscordPlan
            : DiscordPlan.Off;

    // Off Copies Exactly, A Crop Always Encodes
    private bool KeepsClip => Plan == DiscordPlan.Off && !IsCropped;

    // A Crop That Changes The Frame, While A Picture Stays
    private bool IsCropped =>
        Crop is { } crop && KeepsVideo && crop.Changes(Media.Width, Media.Height);

    private bool KeepsVideo =>
        ClipExporter.KeepsVideo(
            Media,
            Controller.Settings.ExportVideo,
            Controller.Settings.ExportAudio,
            Controller.Settings.ExportMicrophone
        );

    // The Frame An Export Starts From, Cropped Or Whole
    private (int Width, int Height) Frame =>
        IsCropped && Crop!.PixelsOf(Media.Width, Media.Height) is var (width, height, _, _)
            ? (width, height)
            : (Media.Width, Media.Height);

    private bool AnyPlanChanges =>
        Enum.GetValues<DiscordPlan>().Any(plan => plan != DiscordPlan.Off && Changes(plan));

    // The Selection As The Size Budget Reads It
    private ExportShape Shape
    {
        get
        {
            // A Plan Budgets Its Full Box, Off The Frame Itself
            var (width, height) =
                Plan == DiscordPlan.Off
                    ? Frame
                    : ClipExporter.OutputSize(Frame.Width, Frame.Height);
            return new(Range.Length, width, height, FrameRate, WithAudio, KeepsVideo);
        }
    }

    private bool KeepsGame => Controller.Settings.ExportAudio && Media.HasAudio;

    private bool KeepsMicrophone => Controller.Settings.ExportMicrophone;

    private double GameVolume => Controller.Settings.ExportGameVolume;

    private double MicrophoneVolume => Controller.Settings.ExportMicrophoneVolume;

    // Both Tracks At Their Own Levels Rebuild The Mix
    private bool IsBalanced =>
        Media.HasSeparateTracks && Track == 0 && (GameVolume != 1 || MicrophoneVolume != 1);

    private int? Track => ClipExporter.TrackOf(Media, KeepsGame, KeepsMicrophone);

    private bool WithAudio => Track is not null;

    // The Clip Itself Keeps Every Track, An Export One
    private int CopiedTracks =>
        OnlyCopies ? Media.AudioTrackCount
        : WithAudio ? 1
        : 0;

    // What Plays, Never A Cut's Hidden Pre-Roll, Unlisted The File's
    private double ClipBitsPerSecond =>
        Media.Packets is { } packets
            ? 8
                * (
                    (
                        SizeBudget.PictureBytes(packets, TrimRange.Whole(Media.Duration))
                        / Media.Duration
                    ) + (Media.AudioTrackCount * packets.AudioBytesPerSecond)
                )
            : Clip.SizeBytes * 8 / Media.Duration;

    // A Copy's Packets, Weighed Once Per Range, Sound And Picture
    private readonly Dictionary<(int Tracks, bool Video), long> _copies = [];
    private TrimRange? _copiedRange;

    // One Picked Track, What A Plan Weighs Against Its Limit
    private long CopyBytes => CopyBytesOf(WithAudio ? 1 : 0);

    // What The Budget Was Last Worked Out For
    private (DiscordPlan Plan, ExportShape Shape, long CropBytes)? _budgetFor;

    private long CopyBytesOf(int tracks)
    {
        // Weighed From The Packets Once Per Range And Streams
        if (_copiedRange != Range)
        {
            _copiedRange = Range;
            _copies.Clear();
        }

        if (!_copies.TryGetValue((tracks, KeepsVideo), out var bytes))
        {
            bytes = ClipExporter.CopyBytes(Clip.SizeBytes, Media, Range, tracks, KeepsVideo);
            _copies[(tracks, KeepsVideo)] = bytes;
        }

        return bytes;
    }

    public void ShowLive(TrimRange? range, CropBox? crop)
    {
        // Only The Numbers Follow A Drag, Never The Whole Editor
        (_liveRange, Crop) = (range, crop);
        StateHasChanged();
    }

    private bool IsExporting => Exports.IsExporting(Clip.Path);

    // Another Clip's Export Runs, This One Would Wait Its Turn
    private bool OtherExporting => Exports.Running is not null && !IsExporting;

    private ExportProgress? ShownProgress =>
        !IsExporting ? null
        : Exports.IsWaiting(Clip.Path) ? new("Waiting for Another Export", 0)
        : Exports.Progress;

    // This Clip's Newest Export, Kept Past Leaving And Restarts
    private string? ExportedFile =>
        Exports.Last is { File: { } file } last
        && last.Request.SourcePath == Clip.Path
        && File.Exists(file)
            ? file
        : Clip.Export is { } remembered && File.Exists(remembered) ? remembered
        : null;

    private bool IsCut => Range.CutsShort(Media.Duration);

    // Off With Its Sound As Recorded, An Export Just Copies
    private bool OnlyCopies =>
        KeepsClip
        && KeepsVideo
        && !IsBalanced
        && (Media.HasSeparateTracks ? Track == 0 : WithAudio == Media.HasAudio);

    // Once Exported, The Same Selection Reads Again
    private string ExportText =>
        OnlyCopies ? (IsCut ? "Save and Copy" : "Copy File")
        : IsCut ? "Export and Save"
        : ExportedFile is null ? "Export"
        : "Export Again";

    private bool CanExport =>
        OnlyCopies
            ? !Busy && (!IsCut || CanSaveCut)
            : !IsExporting && !Busy && Range.IsValidFor(Media.Duration) && !IsTooLong;

    private (int Width, int Height) OutputSize =>
        Budget is { } budget
            ? ClipExporter.OutputSize(Frame.Width, Frame.Height, budget.Lines)
            : Frame;

    private double ClipFrameRate => ClipExporter.FrameRateOf(Media, planned: false);

    private double FrameRate => ClipExporter.FrameRateOf(Media, planned: Plan != DiscordPlan.Off);

    // Null For An Exact Copy, Crops Keep Bits Per Pixel
    private ExportBudget? Budget
    {
        get
        {
            // Every Row Reads It, Only New Inputs Work It Out
            var key = (Plan, Shape, CropBytes);
            if (key != _budgetFor)
            {
                _budgetFor = key;
                field =
                    SizeBudget.LimitOf(key.Plan) is { } limit ? SizeBudget.Plan(limit, key.Shape)
                    : IsCropped ? SizeBudget.Keep(key.CropBytes / 1e6, key.Shape)
                    : null;
            }

            return field;
        }
    }

    // What A Crop Weighs Under Off, None Without One
    private long CropBytes =>
        IsCropped ? ClipExporter.CropBytes(Clip.SizeBytes, Media, Range, Crop!, WithAudio) : 0;

    // Ponytail: Never Above Its Source, Thin Foreign Sound Reads Low
    private long ExportBytes =>
        Budget is not { } budget
            ? CopyBytesOf(CopiedTracks)
            : Math.Min(budget.TypicalBytes, IsCropped ? CropBytes : CopyBytesOf(CopiedTracks));

    // Too Long Exports Nothing, So The Rows Show The Clip
    private string ClipSizeText => Formatter.FormatSize(Clip.SizeBytes);

    private string ExportSizeText => IsTooLong ? ClipSizeText : Formatter.FormatSize(ExportBytes);

    private string ClipResolutionText => Formatter.FormatResolution(Media.Width, Media.Height);

    // Sound Alone Has No Picture To Size
    private string ExportResolutionText =>
        IsTooLong ? ClipResolutionText
        : !KeepsVideo ? NoneText
        : Formatter.FormatResolution(OutputSize.Width, OutputSize.Height);

    private string ClipFrameRateText => Formatter.FormatFrameRate(ClipFrameRate);

    // A Long Selection's Budget Can Lower The Frame Rate
    private double ExportFrameRate => Math.Min(FrameRate, Budget?.FrameRate ?? FrameRate);

    private string ExportFrameRateText =>
        IsTooLong ? ClipFrameRateText
        : !KeepsVideo ? NoneText
        : Formatter.FormatFrameRate(ExportFrameRate);

    // Encodes Take The Last Export's Pace, Copies A Moment
    private string ExportTimeText =>
        IsTooLong ? "-"
        : OnlyCopies && !IsCut ? NoneText
        : Budget is not null
            ? $"About {Formatter.FormatDuration(ClipExporter.EncodeSeconds((OutputSize.Width, OutputSize.Height, ExportFrameRate), ClipFrameRate, Range.Length, KeepsVideo, WithAudio))}"
        : $"About {Formatter.FormatDuration(ClipExporter.CopySeconds(ExportBytes))}";

    private const string NoneText = "None";

    private string ClipBitrateText => Formatter.FormatBitrate(ClipBitsPerSecond);

    private static string[] WordsOf(string file)
    {
        // Breaks Only Between Words, Never Inside A Time
        return Path.GetFileName(file).Split(' ');
    }

    private static bool IsSoundOnly(string file)
    {
        // Sound Alone Is Named .ogg Or .m4a, Pictures .mp4
        return !file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    // Copies Keep The Clip's Rate, Opus Its Sound's, Encodes Theirs
    private string BitrateText =>
        IsTooLong ? ClipBitrateText
        : Budget is not { } budget ? Formatter.FormatBitrate(CopyBitsPerSecond)
        : !KeepsVideo
            ? Formatter.FormatBitrate(Math.Min(budget.AudioBitsPerSecond, SoundBitsPerSecond))
        : Formatter.FormatBitrate(ExportBytes * 8 / Range.Length);

    // The Whole Clip's Rate, Less Each Stream Left Out
    private double CopyBitsPerSecond =>
        !KeepsVideo
            ? SoundBitsPerSecond * CopiedTracks
            : ClipBitsPerSecond - ((Media.AudioTrackCount - CopiedTracks) * SoundBitsPerSecond);

    // One Track's Measured Sound, The Usual Rate Unmeasured
    private double SoundBitsPerSecond =>
        Media.Packets is { } packets
            ? packets.AudioBytesPerSecond * 8
            : SizeBudget.AudioBitsPerSecond;

    // Off Keeps The Clip's Bits, Never Too Long Or Soft
    private BudgetVerdict? Verdict => Plan == DiscordPlan.Off ? null : Budget?.Verdict;

    private bool IsTooLong => Verdict == BudgetVerdict.TooSmall;

    // Names What The Plan Fits And How To Fit More
    private string TooLongText =>
        $"Too long for {SizeBudget.NameOf(Plan)}, which fits about {Formatter.FormatDuration(SizeBudget.LongestSeconds(SizeBudget.LimitOf(Plan) ?? 0, Shape))}, pick a shorter selection{(KeepsVideo && WithAudio ? " or leave out the video" : "")}";

    // A Waiting Turn Or A Soft Picture, Under The Rows
    private string? HintText =>
        OtherExporting && !OnlyCopies ? "Another clip is exporting, this one waits its turn"
        : Verdict == BudgetVerdict.Soft
            ? "Fast motion will look soft at this size, a shorter selection keeps it sharper"
        : null;

    // A Fitting Plan Copies Like Off, Sound Alone Becomes Opus
    private bool Changes(DiscordPlan plan)
    {
        return SizeBudget.LimitOf(plan) is not { } limit
            || !KeepsVideo
            || (IsCropped ? CropBytes : CopyBytes) > limit * 1_000_000;
    }

    private void SetPlan(DiscordPlan plan)
    {
        Controller.UpdateSettings(settings => settings with { ExportDiscordPlan = plan });
    }

    private async Task SetStreamsAsync(Func<UserSettings, UserSettings> change)
    {
        // Remembered, And The Player's Crop Box Follows
        Controller.UpdateSettings(change);
        await StreamsChanged.InvokeAsync();
    }

    private Task ToggleAudio() =>
        SetStreamsAsync(settings => settings with { ExportAudio = !KeepsGame });

    // Leaving Out The Video Needs Sound To Keep
    private bool VideoLocked => IsExporting || Busy || (KeepsVideo && !WithAudio);

    private bool SoundLocked(bool game)
    {
        // Without Video, The Last Sound Track Stays
        return IsExporting
            || Busy
            || (
                !KeepsVideo
                && ClipExporter.TrackOf(
                    Media,
                    game ? !KeepsGame : KeepsGame,
                    game ? KeepsMicrophone : !KeepsMicrophone
                )
                    is null
            );
    }

    private Task ToggleVideo() =>
        SetStreamsAsync(settings => settings with { ExportVideo = !KeepsVideo });

    private Task ToggleMicrophone() =>
        SetStreamsAsync(settings => settings with { ExportMicrophone = !KeepsMicrophone });

    private Task SetGameVolumeAsync(double level) =>
        SetStreamsAsync(settings =>
            settings with
            {
                ExportAudio = level > 0,
                ExportGameVolume = level > 0 ? level : settings.ExportGameVolume,
            }
        );

    private Task SetMicrophoneVolumeAsync(double level) =>
        SetStreamsAsync(settings =>
            settings with
            {
                ExportMicrophone = level > 0,
                ExportMicrophoneVolume = level > 0 ? level : settings.ExportMicrophoneVolume,
            }
        );

    private async Task SaveCutAsync()
    {
        var range = Range;
        if (await SettledPath() is { } path)
        {
            await SaveCut(path, range);
        }
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
        var (range, cut) = (Range, IsCut);
        if (await SettledPath() is not { } path)
        {
            return;
        }

        if (!cut || await SaveCut(path, range))
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

        // Read Before A Rename Replaces This Panel
        var (cut, keepOriginal) = (IsCut, !Controller.Settings.DeleteOriginals);
        var (range, plan, frameRate, track, video) = (Range, Plan, FrameRate, Track, KeepsVideo);
        var (crop, cropBytes) = (IsCropped ? Crop : null, CropBytes);
        var (game, microphone) = IsBalanced ? (GameVolume, MicrophoneVolume) : (1.0, 1.0);
        var folder = Controller.Settings.ExportsDirectory;
        if (await SettledPath() is not { } path)
        {
            return;
        }

        // Runs On Past Leaving, The App Bar Shows It
        var request = new ExportRequest(
            path,
            range,
            plan,
            frameRate,
            track,
            folder,
            Crop: crop,
            Video: video,
            GameVolume: game,
            MicrophoneVolume: microphone,
            CropBytes: cropBytes
        );
        _ = Exports.RunAsync(request, cut, keepOriginal);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_revealResult)
        {
            _revealResult = false;
            await JavaScript.CallAsync("letsClip.reveal", "exported");
        }

        // The Shown Export's Facts, Read From Its Own File Once
        if (ExportedFile != _shownExport)
        {
            _shownExport = ExportedFile;
            (_exportFacts, _leftOut) = _shownExport is { } file
                ? await FactsOfAsync(file)
                : (NoFacts, null);
            StateHasChanged();
        }
    }

    private async Task<(string Facts, (string Icon, string Text)? LeftOut)> FactsOfAsync(
        string file
    )
    {
        try
        {
            var size = Formatter.FormatSize(new FileInfo(file).Length);
            var export = await Task.Run(() => MediaProbe.ProbeAsync(file, CancellationToken.None));
            var facts =
                IsSoundOnly(file) ? $"{size} · Sound Only"
                : export is null ? size
                : $"{size} · {Formatter.FormatResolution(export.Width, export.Height)} · {Formatter.FormatFrameRate(export.FrameRate)}";
            return (facts, export is null ? null : LeftOutOf(export));
        }
        catch (Exception exception)
            when (exception is TimeoutException or Win32Exception or IOException)
        {
            // An Unreadable Export Shows Only Its Name
            return (NoFacts, null);
        }
    }

    private (string Icon, string Text)? LeftOutOf(MediaInfo export)
    {
        // What The Export Left Out Of The Clip's Sound
        return Media.HasAudio && !export.HasAudio ? (Icons.Material.Rounded.VolumeOff, "No Sound")
            : Media.HasSeparateTracks && export.AudioName == AudioTracks.Game
                ? (Icons.Material.Rounded.MicOff, "No Microphone")
            : Media.HasSeparateTracks && export.AudioName == AudioTracks.Microphone
                ? (Icons.Material.Rounded.VolumeOff, "No Game Audio")
            : null;
    }

    private void Cancel()
    {
        Exports.Cancel(Clip.Path);
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
