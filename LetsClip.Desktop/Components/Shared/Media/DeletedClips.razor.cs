using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class DeletedClips
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private bool _open;
    private bool _listing;

    // Read When Opened, A Restored Clip Leaves The List
    private IReadOnlyList<DeletedClip>? _deleted;
    private DeletedClip? _restoring;

    private bool RestoreAllDisabled => _deleted is not { Count: > 0 } || _restoring is not null;

    private async Task OpenAsync()
    {
        // The Bin Is Read Off The Window's Thread, Then Shown
        _listing = true;
        var folder = Controller.Settings.ClipsDirectory;
        _deleted = await Task.Run(() => ClipLibrary.DeletedFrom(folder));
        (_listing, _open) = (false, true);
    }

    private void Close()
    {
        _open = false;
    }

    private static string DetailOf(DeletedClip clip)
    {
        // Every Row Says When It Was Recorded, Whole Clips Too
        var recorded = Formatter.FormatDateTime(clip.RecordedAt);
        return clip.IsWholeClip ? $"Whole Clip · {recorded}" : recorded;
    }

    private async Task RestoreAsync(IReadOnlyList<DeletedClip> clips)
    {
        // One At A Time, The Watcher Brings Cards Back
        foreach (var clip in clips.ToList())
        {
            _restoring = clip;
            StateHasChanged();
            try
            {
                await Task.Run(() => ClipLibrary.Restore(clip));
                _deleted = [.. (_deleted ?? []).Where(shown => shown != clip)];
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                Snackbar.Add("The clip could not be restored, it may be in use", Severity.Warning);
                break;
            }
        }

        _restoring = null;
    }

    private void OpenRecycleBin()
    {
        // Everything Else Waits In Windows' Own Bin
        _open = false;
        Explorer.Open("shell:RecycleBinFolder");
    }
}
