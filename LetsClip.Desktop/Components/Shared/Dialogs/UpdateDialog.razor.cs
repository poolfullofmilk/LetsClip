using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Shared.Common;
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
    private IReadOnlyList<string>? _changes;
    private TransferProgress? _progress;

    private string Title => $"LetsClip {Release.Tag} is Out";

    protected override async Task OnInitializedAsync()
    {
        // Every Commit Since This Build, Read While The Dialog Shows
        _changes = await Updater.ChangesAsync(AppInfo.Commit, Release.Tag);
    }

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
            // The Tray Downloads, Starts The New Exe And Exits
            if (Application.Current is App { Tray: { } tray })
            {
                await tray.InstallUpdateAsync(
                    Release,
                    new Progress<(long Received, long Total)>(progress =>
                        _progress?.Report(progress)
                    )
                );
            }
        }
        catch (InvalidDataException exception)
        {
            // An Unverified Download Never Runs, The Release Page Opens Instead
            s_logger.Error(exception, "Failed To Verify Update");
            Snackbar.Add("The download could not be verified", Severity.Warning);
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
