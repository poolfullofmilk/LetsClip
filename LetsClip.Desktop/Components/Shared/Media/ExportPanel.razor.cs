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

    // Back To The Whole Picture
    [Parameter]
    public EventCallback CropReset { get; set; }

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

    // The Shown Export's Changes And Which File They Came From
    private string? _changesOf;
    private IReadOnlyList<(string Icon, string Text, bool LeftOut)> _changes = [];

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

    private double ClipBitsPerSecond => Clip.SizeBytes * 8 / Media.Duration;

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
            var key = (Plan, Shape, CropBytes: IsCropped ? CropBytes : 0L);
            if (key != _budgetFor)
            {
                _budgetFor = key;
                field =
                    SizeBudget.LimitOf(key.Plan) is { } limit ? SizeBudget.Plan(limit, key.Shape)
                    : key.CropBytes > 0 ? SizeBudget.Keep(key.CropBytes / 1e6, key.Shape)
                    : null;
            }

            return field;
        }
    }

    private long CropBytes =>
        ClipExporter.CropBytes(Clip.SizeBytes, Media.Duration, Media, Range.Length, Crop!);

    // A Copy's Weight, Or Where An Encode Lands
    private long ExportBytes => Budget?.TypicalBytes ?? CopyBytesOf(CopiedTracks);

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
            ? $"About {Formatter.FormatDuration(ClipExporter.EncodeSeconds(OutputSize.Width, OutputSize.Height, ExportFrameRate, Range.Length, KeepsVideo))}"
        : $"About {Formatter.FormatDuration(ClipExporter.CopySeconds(ExportBytes))}";

    private const string NoneText = "None";

    private string ClipBitrateText => Formatter.FormatBitrate(ClipBitsPerSecond);

    private static string[] WordsOf(string file)
    {
        // Breaks Only Between Words, Never Inside A Time
        return Path.GetFileName(file).Split(' ');
    }

    // A Copy Keeps The Clip's Rate, An Encode Its Most
    private string BitrateText =>
        IsTooLong ? ClipBitrateText
        : Budget is null ? Formatter.FormatBitrate(CopyBitsPerSecond)
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

    private bool IsTooLong => Budget?.Verdict == BudgetVerdict.TooSmall;

    // Names What The Plan Fits And How To Fit More
    private string TooLongText =>
        $"Too long for {SizeBudget.NameOf(Plan)}, which fits about {Formatter.FormatDuration(SizeBudget.LongestSeconds(SizeBudget.LimitOf(Plan) ?? 0, Shape))}, pick a shorter selection{(KeepsVideo && WithAudio ? " or leave out the video" : "")}";

    // A Waiting Turn Or A Soft Picture, Under The Rows
    private string? HintText =>
        OtherExporting && !OnlyCopies ? "Another clip is exporting, this one waits its turn"
        : Budget?.Verdict == BudgetVerdict.Soft
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
        var crop = IsCropped ? Crop : null;
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
            MicrophoneVolume: microphone
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

        // The Shown Export's Changes, Read From Its Own File Once
        if (ExportedFile != _changesOf)
        {
            _changesOf = ExportedFile;
            MediaInfo? export = null;
            try
            {
                export = _changesOf is { } file
                    ? await Task.Run(() => MediaProbe.ProbeAsync(file, CancellationToken.None))
                    : null;
            }
            catch (Exception exception) when (exception is TimeoutException or Win32Exception)
            {
                // An Unreadable Export Just Shows No Changes
            }

            _changes = export is null ? [] : [.. ChangesOf(export)];
            StateHasChanged();
        }
    }

    private IEnumerable<(string Icon, string Text, bool LeftOut)> ChangesOf(MediaInfo export)
    {
        // What The Export Left Out Or Made Smaller
        if (!export.HasVideo)
        {
            yield return (Icons.Material.Rounded.VideocamOff, "Sound Only", true);
        }
        else
        {
            // A Crop Changed The Frame's Shape, So Its Size Too
            var cropped =
                Math.Abs(
                    (export.Width / (double)export.Height) - (Media.Width / (double)Media.Height)
                ) > 0.02;
            if (cropped)
            {
                yield return (Icons.Material.Rounded.Crop, "Cropped", false);
            }

            if (cropped || export.Width < Media.Width)
            {
                yield return (
                    Icons.Material.Rounded.PhotoSizeSelectSmall,
                    Formatter.FormatResolution(export.Width, export.Height),
                    false
                );
            }

            if (Math.Round(export.FrameRate) < ClipFrameRate)
            {
                yield return (
                    Icons.Material.Rounded.SlowMotionVideo,
                    Formatter.FormatFrameRate(export.FrameRate),
                    false
                );
            }
        }

        if (Media.HasAudio && !export.HasAudio)
        {
            yield return (Icons.Material.Rounded.VolumeOff, "No Sound", true);
        }
        else if (Media.HasSeparateTracks && export.AudioName == AudioTracks.Game)
        {
            yield return (Icons.Material.Rounded.MicOff, "No Microphone", true);
        }
        else if (Media.HasSeparateTracks && export.AudioName == AudioTracks.Microphone)
        {
            yield return (Icons.Material.Rounded.VolumeOff, "No Game Audio", true);
        }
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
