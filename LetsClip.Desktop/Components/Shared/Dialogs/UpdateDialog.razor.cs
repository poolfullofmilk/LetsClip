using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Integration;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NLog;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class UpdateDialog
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    [Parameter]
    public required ReleaseInfo Release { get; set; }

    private bool _isInstalling;

    // Bytes So Far, Zero Total While No Length Is Sent
    private long _received;
    private long _total;
    private int _percent;

    private string Title => $"LetsClip v{Release.Tag.TrimStart('v', 'V')} Is Out";

    private string ProgressText =>
        _total > 0 && _received >= _total ? "Starting the new version"
        : _total > 0 ? $"{Formatter.FormatSize(_received)} of {Formatter.FormatSize(_total)}"
        : Formatter.FormatSize(_received);

    private async Task InstallAsync()
    {
        // Debug Or Renamed Builds Have No Release Exe To Replace
        if (!Updater.CanInstall)
        {
            OpenReleasePage();
            return;
        }

        _isInstalling = true;
        await SetClosableAsync(false);
        try
        {
            // Off The Window's Thread, So The Window Never Freezes
            var progress = new Progress<(long, long)>(ShowProgress);
            Updater.Launch(await Task.Run(() => Updater.DownloadAsync(Release, progress)));

            // The New Exe Waits For This One To Exit
            if (Application.Current.MainWindow is MainWindow window)
            {
                await window.ExitAsync();
            }
        }
        catch (InvalidDataException exception)
        {
            // An Unverified Download Never Runs, The Release Page Opens Instead
            s_logger.Error(exception, "Failed To Verify Update");
            Snackbar.Add("The download did not match GitHub's checksum", Severity.Warning);
            OpenReleasePage();
        }
        catch (UnauthorizedAccessException exception)
        {
            // A Protected Folder Like Program Files Takes A Manual Download
            s_logger.Error(exception, "Failed To Update, The App Folder Is Protected");
            Snackbar.Add("LetsClip cannot write to its own folder", Severity.Warning);
            OpenReleasePage();
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or IOException
                        or OperationCanceledException
                        or Win32Exception
            )
        {
            s_logger.Error(exception, "Failed To Update");
            Snackbar.Add("The update failed, try again later", Severity.Error);
            _isInstalling = false;
            await SetClosableAsync(true);
        }
    }

    private void ShowProgress((long Received, long Total) progress)
    {
        // Redraws Once Per Percent, Not Per Chunk
        var (received, total) = progress;
        var percent = total > 0 ? (int)(received * 100 / total) : 0;
        var moved =
            total != _total || (total > 0 ? percent != _percent : received - _received >= 1048576);
        if (!moved)
        {
            return;
        }

        _received = received;
        _total = total;
        _percent = percent;
        _ = InvokeAsync(StateHasChanged);
    }

    private Task SetClosableAsync(bool closable)
    {
        return MudDialog.SetOptionsAsync(MudDialog.Options with { CloseOnEscapeKey = closable });
    }

    private void OpenReleasePage()
    {
        Explorer.Open(AppInfo.LatestReleaseUrl);
        MudDialog.Close();
    }
}
