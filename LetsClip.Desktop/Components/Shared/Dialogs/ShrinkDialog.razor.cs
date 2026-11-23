using System.ComponentModel;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NLog;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class ShrinkDialog : IDisposable
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    // Rows Keep Their Order, Newest First Like The Clips Page
    private List<ClipInfo>? _rows;
    private readonly HashSet<string> _picked = [];
    private Shrinking? _running;
    private CancellationTokenSource? _cancel;

    public static Task<IDialogReference> ShowAsync(IDialogService dialogService)
    {
        return dialogService.ShowAsync<ShrinkDialog>(
            (string?)null,
            new DialogOptions
            {
                MaxWidth = MaxWidth.Small,
                FullWidth = true,
                BackdropClick = false,
            }
        );
    }

    // Where The Originals Go Follows Delete Originals When Cutting
    private string NoteText =>
        Controller.Settings.DeleteOriginals
            ? "Picked clips become HEVC at about two fifths of their size with no visible change, the originals go to the Recycle Bin"
            : "Picked clips become HEVC at about two fifths of their size with no visible change, the originals stay in Kept Originals";

    private string AllIcon =>
        _picked.Count == 0 ? Icons.Material.Rounded.CheckBoxOutlineBlank
        : _picked.Count == _rows?.Count ? Icons.Material.Rounded.CheckBox
        : Icons.Material.Rounded.IndeterminateCheckBox;

    private long SavedBytes =>
        _rows
            ?.Where(clip => _picked.Contains(clip.Path))
            .Sum(clip => clip.SizeBytes - (ClipLibrary.ShrunkBytesOf(clip) ?? clip.SizeBytes))
        ?? 0;

    private string? SavingText =>
        _picked.Count > 0 ? $"Saves About {Formatter.FormatSize(SavedBytes)}" : null;

    private string ShrinkText =>
        _picked.Count switch
        {
            0 => "Shrink Clips",
            1 => "Shrink 1 Clip",
            var count => $"Shrink {count} Clips",
        };

    protected override async Task OnInitializedAsync()
    {
        // Clips Worth Shrinking, Those Older Than A Week Picked
        var clips = await Task.Run(() => Controller.Library.ListAsync(CancellationToken.None));
        var weekAgo = DateTimeOffset.Now - ClipLibrary.ShrinkAge;
        _rows =
        [
            .. clips
                .Where(clip =>
                    ClipLibrary.ShrunkBytesOf(clip) is not null
                    && !Controller.Exports.IsExporting(clip.Path)
                )
                .OrderByDescending(clip => clip.RecordedAt),
        ];
        _picked.UnionWith(_rows.Where(clip => clip.RecordedAt < weekAgo).Select(clip => clip.Path));
    }

    private static string TitleOf(ClipInfo clip)
    {
        // Its Name, Else Its Game Or Monitor, Like Recently Deleted
        return clip.CustomTitle ?? clip.Source;
    }

    private static string FactsOf(ClipInfo clip)
    {
        // Its Date, Then Its Size Now And After
        var after = ClipLibrary.ShrunkBytesOf(clip) ?? clip.SizeBytes;
        return $"{Formatter.FormatDateTime(clip.RecordedAt)} · {Formatter.FormatSize(clip.SizeBytes)} to About {Formatter.FormatSize(after)}";
    }

    private void Pick(ClipInfo clip, bool on)
    {
        if (on)
        {
            _picked.Add(clip.Path);
        }
        else
        {
            _picked.Remove(clip.Path);
        }
    }

    private void ToggleAll()
    {
        // All On, Or All Off Once They All Are
        if (_rows is null)
        {
            return;
        }

        var allOn = _picked.Count == _rows.Count;
        _picked.Clear();
        if (!allOn)
        {
            _picked.UnionWith(_rows.Select(clip => clip.Path));
        }
    }

    private async Task ShrinkAsync()
    {
        // One Clip At A Time, Until Done Or Cancelled
        var picked = _rows?.Where(clip => _picked.Contains(clip.Path)).ToList() ?? [];
        var keepOriginal = !Controller.Settings.DeleteOriginals;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        await MudDialog.SetOptionsAsync(MudDialog.Options with { CloseOnEscapeKey = false });
        var (shrunk, saved, failed) = (0, 0L, 0);
        for (var index = 0; index < picked.Count && !token.IsCancellationRequested; index++)
        {
            var clip = picked[index];
            _running = new Shrinking(TitleOf(clip), index + 1, picked.Count, 0);
            StateHasChanged();
            if (Controller.Exports.IsExporting(clip.Path))
            {
                continue;
            }

            try
            {
                var progress = new Progress<double>(fraction =>
                {
                    _running = _running with { Fraction = fraction };
                    StateHasChanged();
                });
                saved += await Task.Run(
                    () => ClipLibrary.ShrinkAsync(clip.Path, keepOriginal, progress, token),
                    token
                );
                shrunk++;
            }
            catch (OperationCanceledException)
            {
                // Cancelled, The Clip Being Shrunk Stays As It Was
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or InvalidDataException
                            or UnauthorizedAccessException
                            or TimeoutException
                            or Win32Exception
                            or ExportException
                )
            {
                // A Failing Clip Stays As It Was, Others Go On
                s_logger.Warn($"Failed To Shrink {clip.Name} {exception.GetType().Name}");
                failed++;
            }
        }

        _running = null;
        MudDialog.Close();
        Snackbar.Add(
            $"Shrank {shrunk} {(shrunk == 1 ? "clip" : "clips")} by {Formatter.FormatSize(saved)}{(failed > 0 ? $", {failed} could not be shrunk" : "")}{(keepOriginal ? "" : ", the originals are in the Recycle Bin")}",
            failed > 0 ? Severity.Warning : Severity.Success
        );
    }

    private void Cancel()
    {
        _cancel?.Cancel();
    }

    public void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record Shrinking(string Title, int Index, int Count, double Fraction);
}
