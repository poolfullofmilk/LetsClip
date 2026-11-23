using System.ComponentModel;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Integration;
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
    private IReadOnlyList<(string Icon, string Text)> _leftOut = [];

    // Holds The Facts Line's Height Until They Are Read
    private const string NoFacts = " ";

    private ExportRunner Exports => Controller.Exports;

    // The Saved Plan, Off While The Selection Already Fits
    private DiscordPlan Plan =>
        Changes(Controller.Settings.ExportDiscordPlan)
            ? Controller.Settings.ExportDiscordPlan
            : DiscordPlan.Off;

    // Off Copies Exactly, A Crop Always Encodes
    private bool KeepsClip => Plan == DiscordPlan.Off && !IsCropped && !Captioned;

    // The Clip's Words, Null Until Heard, Empty For None
    private IReadOnlyList<SpokenWord>? _words;
    private bool _hearing;
    private bool _wordsRead;

    private bool CaptionsOn => Controller.Settings.ExportCaptions;

    // Drawn Onto The Picture, So The Export Encodes
    private bool Captioned => CaptionsOn && KeepsVideo && _words is { Count: > 0 };

    // One Already On Can Always Be Turned Off
    private bool CaptionsLocked =>
        IsExporting || Busy || (!CaptionsOn && (!KeepsVideo || _words is { Count: 0 }));

    private string? CaptionsDetail =>
        _hearing ? null
        : !KeepsVideo ? "Not Used"
        : _words is { Count: 0 } ? "No Speech Heard"
        : null;

    // A Crop That Changes The Frame, While A Picture Stays
    private bool IsCropped =>
        Crop is { } crop && KeepsVideo && crop.Changes(Media.Width, Media.Height);

    private bool KeepsVideo =>
        ClipExporter.KeepsVideo(Media, Controller.Settings.ExportVideo, Sound);

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

    private SoundMix Sound => Controller.Settings.ExportSound;

    // One Track Copied, Or Parts Mixed Anew At Their Levels
    private (int? Track, IReadOnlyList<SoundPick>? Balance) Picked =>
        ClipExporter.SoundOf(Media, Sound);

    private bool IsBalanced => Picked.Balance is not null;

    private int? Track => Picked.Track;

    // Game, Voice Chat Then Microphone, The Order The Row Shows
    private static readonly string[] s_partOrder =
    [
        AudioTracks.Game,
        AudioTracks.Voice,
        AudioTracks.Microphone,
    ];

    private IEnumerable<string> ShownParts => s_partOrder.Where(part => Media.Parts.Contains(part));

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
            // Past The End, What Came After Weighs Like The Rest
            var within = new TrimRange(Range.Start, Math.Min(Range.End, Media.Duration));
            bytes = ClipExporter.CopyBytes(Clip.SizeBytes, Media, within, tracks, KeepsVideo);
            if (Range.End > Media.Duration && within.Length > 0)
            {
                bytes += (long)(bytes / within.Length * (Range.End - Media.Duration));
            }

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
            : !IsExporting
                && !Busy
                && Range.IsValidFor(Media.Duration)
                && !IsTooLong
                && !(CaptionsOn && _hearing);

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
            var key = (Plan, Shape, OffBytes);
            if (key != _budgetFor)
            {
                _budgetFor = key;
                field =
                    SizeBudget.LimitOf(key.Plan) is { } limit ? SizeBudget.Plan(limit, key.Shape)
                    : IsCropped || Captioned ? SizeBudget.Keep(key.OffBytes / 1e6, key.Shape)
                    : null;
            }

            return field;
        }
    }

    // What A Crop Weighs Under Off, None Without One
    // What An Encode Under Off Weighs, Crop Or Frame
    private long OffBytes =>
        IsCropped ? CropBytes
        : Captioned ? CopyBytesOf(WithAudio ? 1 : 0)
        : 0;

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

    // Leaving Out The Video Needs Sound To Keep
    private bool VideoLocked => IsExporting || Busy || (KeepsVideo && !WithAudio);

    private bool SoundLocked(string part)
    {
        // Without Video, The Last Sound Part Stays
        return IsExporting
            || Busy
            || (!KeepsVideo && ClipExporter.SoundOf(Media, Toggled(part)).Track is null);
    }

    private SoundMix Toggled(string part)
    {
        var sound = Sound;
        return part switch
        {
            AudioTracks.Microphone => sound with { Microphone = !sound.Microphone },
            AudioTracks.Voice => sound with { Voice = !sound.Voice },
            _ => sound with { Game = !sound.Game },
        };
    }

    private bool Slides(string part)
    {
        // A Slider Rises Only While Another Part Is Kept
        return ShownParts.Any(other => other != part && Sound.Keeps(other));
    }

    private string VolumeText(string part)
    {
        var name = part == AudioTracks.Game ? "Game Audio" : part;
        return Sound.Keeps(part) ? $"Mute {name}" : $"Unmute {name}";
    }

    private static string? PartIcon(string part, bool on)
    {
        // The Game Keeps The Speaker, Its Level Drawn In It
        return part switch
        {
            AudioTracks.Microphone => on
                ? Icons.Material.Rounded.Mic
                : Icons.Material.Rounded.MicOff,
            AudioTracks.Voice => on
                ? Icons.Material.Rounded.Headset
                : Icons.Material.Rounded.HeadsetOff,
            _ => null,
        };
    }

    private Task ToggleVideo() =>
        SetStreamsAsync(settings => settings with { ExportVideo = !KeepsVideo });

    private Task TogglePartAsync(string part) =>
        SetStreamsAsync(settings => WithPart(settings, part, !Sound.Keeps(part), null));

    private Task SetLevelAsync(string part, double level) =>
        SetStreamsAsync(settings => WithPart(settings, part, level > 0, level > 0 ? level : null));

    private static UserSettings WithPart(
        UserSettings settings,
        string part,
        bool kept,
        double? level
    )
    {
        // Sliding To Zero Mutes, The Last Level Stays For Unmuting
        return part switch
        {
            AudioTracks.Microphone => settings with
            {
                ExportMicrophone = kept,
                ExportMicrophoneVolume = level ?? settings.ExportMicrophoneVolume,
            },
            AudioTracks.Voice => settings with
            {
                ExportVoice = kept,
                ExportVoiceVolume = level ?? settings.ExportVoiceVolume,
            },
            _ => settings with
            {
                ExportAudio = kept,
                ExportGameVolume = level ?? settings.ExportGameVolume,
            },
        };
    }

    private async Task SaveCutAsync()
    {
        var range = Range;
        if (await SettledPath() is { } path)
        {
            await SaveCut(path, range);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        // Words Read Once, Heard Now When Captions Are Wanted
        if (_wordsRead)
        {
            return;
        }

        _wordsRead = true;
        _words = await Task.Run(() => ClipLibrary.MetadataOf(Clip.Path)?.Words);
        if (CaptionsOn && _words is null && Media.HasAudio)
        {
            await HearAsync();
        }
    }

    protected override void OnInitialized()
    {
        Controller.Transcripts.Transcribed += OnTranscribed;
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
        var (crop, cropBytes) = (IsCropped ? Crop : null, OffBytes);
        var captions = Captioned && _words is { } words ? CaptionsOf(words, range) : null;
        var (balance, soundName) = (Picked.Balance, ClipExporter.SoundNameOf(Media, Sound));
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
            Balance: balance,
            Captions: captions,
            CropBytes: cropBytes,
            SoundName: soundName
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
                : (NoFacts, []);
            StateHasChanged();
        }
    }

    private async Task<(
        string Facts,
        IReadOnlyList<(string Icon, string Text)> LeftOut
    )> FactsOfAsync(string file)
    {
        try
        {
            var size = Formatter.FormatSize(new FileInfo(file).Length);
            var export = await Task.Run(() => MediaProbe.ProbeAsync(file, CancellationToken.None));
            var facts =
                IsSoundOnly(file) ? $"{size} · Sound Only"
                : export is null ? size
                : $"{size} · {Formatter.FormatResolution(export.Width, export.Height)} · {Formatter.FormatFrameRate(export.FrameRate)}";
            return (facts, export is null ? [] : LeftOutOf(export));
        }
        catch (Exception exception)
            when (exception is TimeoutException or Win32Exception or IOException)
        {
            // An Unreadable Export Shows Only Its Name
            return (NoFacts, []);
        }
    }

    private List<(string Icon, string Text)> LeftOutOf(MediaInfo export)
    {
        // What The Export Left Out Of The Sound, Each Part
        if (Media.HasAudio && !export.HasAudio)
        {
            return [(Icons.Material.Rounded.VolumeOff, "No Sound")];
        }

        return AudioTracks.PartsOf(export.AudioName) is { } held
            ?
            [
                .. ShownParts
                    .Where(part => !held.Contains(part))
                    .Select(part =>
                        (
                            PartIcon(part, false) ?? Icons.Material.Rounded.VolumeOff,
                            part == AudioTracks.Game ? "No Game Audio" : $"No {part}"
                        )
                    ),
            ]
            : [];
    }

    private void Cancel()
    {
        Exports.Cancel(Clip.Path);
    }

    private bool _posting;

    private async Task PostAsync(string file)
    {
        // To The Connected Channel, Named Like The Clip
        if (_posting || Controller.Settings.DiscordWebhook is not { } webhook)
        {
            return;
        }

        _posting = true;
        var problem = await Task.Run(() =>
            DiscordPost.PostAsync(webhook, file, Clip.Title, CancellationToken.None)
        );
        _posting = false;
        if (problem is null)
        {
            Snackbar.Add("Posted to Discord", Severity.Success);
        }
        else
        {
            Snackbar.Add(problem, Severity.Warning);
        }
    }

    private static Func<int, int, string, CaptionTrack> CaptionsOf(
        IReadOnlyList<SpokenWord> words,
        TrimRange range
    )
    {
        // Drawn At The Export's Own Size Once It Is Known
        var frames = CaptionCards.Of(words, range);
        return (width, height, folder) =>
            CaptionPainter.Paint(frames, range.Length, width, height, folder);
    }

    private async Task SetCaptionsAsync(bool on)
    {
        Controller.UpdateSettings(settings => settings with { ExportCaptions = on });
        if (on && _words is null)
        {
            await HearAsync();
        }
    }

    private async Task HearAsync()
    {
        // Heard Now At Full Speed, The Rows Wait Meanwhile
        if (_hearing)
        {
            return;
        }

        _hearing = true;
        StateHasChanged();
        var path = Clip.Path;
        var words = await Task.Run(() =>
            Controller.Transcripts.TranscribeNowAsync(path, CancellationToken.None)
        );
        _hearing = false;
        if (words is null)
        {
            Snackbar.Add("The speech could not be heard, try again later", Severity.Warning);
        }
        else if (path == Clip.Path)
        {
            _words = words;
        }

        StateHasChanged();
    }

    private void OnTranscribed(string path)
    {
        // Heard In The Background While Shown
        if (path == Clip.Path)
        {
            _ = InvokeAsync(() =>
            {
                _words = ClipLibrary.MetadataOf(path)?.Words;
                StateHasChanged();
            });
        }
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
        Controller.Transcripts.Transcribed -= OnTranscribed;
        Exports.Changed -= OnExportsChanged;
        Exports.Progressed -= OnExportsChanged;
        Exports.Finished -= OnExportFinished;
        GC.SuppressFinalize(this);
    }
}
