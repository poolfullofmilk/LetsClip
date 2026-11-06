using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Pages;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Integration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace LetsClip.Desktop.Components.Layout;

public partial class MainLayout : IDisposable
{
    [Inject]
    public required DesktopShell Shell { get; set; }

    [Inject]
    public required IDialogService DialogService { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private ErrorBoundary? _errorBoundary;
    private ReleaseInfo? _availableUpdate;

    protected override void OnInitialized()
    {
        Shell.OpenClipRequested += OnOpenClipRequested;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        await CheckForUpdatesAsync(false);
    }

    private async Task CheckForUpdatesAsync(bool askedByHand)
    {
        var latest = await Updater.CheckAsync();
        if (latest is null)
        {
            if (askedByHand)
            {
                Snackbar.Add("Unable to check for updates right now", Severity.Warning);
            }

            return;
        }

        if (latest.Version <= AppInfo.Version)
        {
            if (askedByHand)
            {
                Snackbar.Add(
                    $"LetsClip {AppInfo.VersionText} is the latest version",
                    Severity.Success
                );
            }

            return;
        }

        // The Red Dot Stays On The More Menu Until Restart
        _availableUpdate = latest;
        StateHasChanged();
        await DialogService.ShowAsync<UpdateDialog>(
            null,
            new DialogParameters<UpdateDialog> { { dialog => dialog.Release, latest } },
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true }
        );
    }

    private void OnOpenClipRequested(string path)
    {
        _ = InvokeAsync(() => NavigationManager.NavigateTo(ClipRoutes.EditorOf(path)));
    }

    private void RecoverFromError()
    {
        _errorBoundary?.Recover();
        NavigationManager.NavigateTo(ClipRoutes.List);
    }

    private static void OpenLogs()
    {
        Explorer.OpenFolder(AppInfo.LogsDirectory);
    }

    private async Task ShowAboutAsync()
    {
        await DialogService.ShowAsync<AboutDialog>(
            (string?)null,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true }
        );
    }

    public void Dispose()
    {
        Shell.OpenClipRequested -= OnOpenClipRequested;
        GC.SuppressFinalize(this);
    }
}
