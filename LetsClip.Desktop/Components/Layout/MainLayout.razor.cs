using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Components.Pages;
using LetsClip.Desktop.Components.Shared.Dialogs;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Integration;
using Microsoft.AspNetCore.Components;
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

    private SelfHealingBoundary? _errorBoundary;
    private bool _checking;
    private MudMenu? _moreMenu;

    protected override void OnInitialized()
    {
        Shell.OpenClipRequested += OnOpenClipRequested;
        Shell.UpdatesChanged += OnUpdatesChanged;
        if (Shell.TakePendingClip() is { } pending)
        {
            OnOpenClipRequested(pending);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Found While Hidden, Offered Once The Page Is Up
        if (firstRender)
        {
            await OfferUpdatesAsync();
        }
    }

    private void OnUpdatesChanged()
    {
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            await OfferUpdatesAsync();
        });
    }

    private async Task OfferUpdatesAsync()
    {
        // What The Last Update Brought, Then Any Newer One
        if (Shell.TakeChanges() is { } changes)
        {
            await DialogService.ShowAsync<ChangesDialog>(
                null,
                new DialogParameters<ChangesDialog> { { dialog => dialog.Changes, changes } },
                new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true }
            );
        }

        if (Shell.TakeUnofferedUpdate() is { } release)
        {
            await ShowUpdateAsync(release);
        }
    }

    private Task OpenMoreAsync()
    {
        // A MudBlazor Button Keeps Its Press From The Activator
        return _moreMenu?.ToggleMenuAsync(EventArgs.Empty) ?? Task.CompletedTask;
    }

    private async Task CheckForUpdatesAsync()
    {
        // Offline It Waits Ten Seconds, More Spins Meanwhile
        if (_checking)
        {
            return;
        }

        _checking = true;
        ReleaseInfo? latest;
        try
        {
            latest = await Updater.CheckAsync();
        }
        finally
        {
            _checking = false;
        }

        if (latest is null)
        {
            Snackbar.Add("Unable to check for updates right now", Severity.Warning);
            return;
        }

        if (latest.Version <= AppInfo.Version)
        {
            Snackbar.Add($"LetsClip {AppInfo.VersionText} is the latest version", Severity.Success);
            return;
        }

        // Asked By Hand, The Dialog Opens Even If Offered Before
        Shell.ReportUpdate(latest, offered: true);
        await ShowUpdateAsync(latest);
    }

    private async Task ShowUpdateAsync(ReleaseInfo release)
    {
        await DialogService.ShowAsync<UpdateDialog>(
            null,
            new DialogParameters<UpdateDialog> { { dialog => dialog.Release, release } },
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true }
        );
    }

    private void OnOpenClipRequested(string path)
    {
        _ = InvokeAsync(() => NavigationManager.NavigateTo(Clips.EditorOf(path)));
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
        Shell.UpdatesChanged -= OnUpdatesChanged;
        GC.SuppressFinalize(this);
    }
}
