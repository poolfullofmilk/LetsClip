using LetsClip.Desktop.Components.Pages;
using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class ExportChip : IDisposable
{
    [Inject]
    public required CaptureController Controller { get; set; }

    [Inject]
    public required NavigationManager Navigation { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    private ExportRunner Exports => Controller.Exports;

    // The Cut Has No Percent, Everything Before It Does
    private string Text =>
        Exports.Progress.Phase == ExportRunner.CutPhase
            ? ExportRunner.CutPhase
            : $"Exporting {Exports.Progress.Fraction * 100:0}%";

    protected override void OnInitialized()
    {
        Exports.Changed += OnChanged;
        Exports.Progressed += OnChanged;
        Exports.Finished += OnFinished;
    }

    private void OnChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnFinished(ExportOutcome outcome)
    {
        _ = InvokeAsync(() => Announce(outcome));
    }

    private void Announce(ExportOutcome outcome)
    {
        if (outcome.Problem is { } problem)
        {
            Snackbar.Add(problem, Severity.Warning);
            return;
        }

        if (outcome.File is not { } file)
        {
            Snackbar.Add("Export cancelled, nothing was kept", Severity.Info);
            return;
        }

        // Its Own Editor Shows The Export, Elsewhere A Snackbar Does
        if (IsShowing(outcome.Request.SourcePath))
        {
            return;
        }

        Snackbar.Add(
            outcome.SavedCut ? "Exported and saved the cut" : "Exported",
            Severity.Success,
            options =>
            {
                options.Action = "Copy File";
                options.OnClick = _ =>
                {
                    DesktopShell.CopyFile(file, Snackbar);
                    return Task.CompletedTask;
                };
            }
        );
    }

    private bool IsShowing(string path)
    {
        // Compared Unescaped, Brackets In A Name Escape Either Way
        return string.Equals(
            Uri.UnescapeDataString(Navigation.ToBaseRelativePath(Navigation.Uri)),
            Uri.UnescapeDataString(Clips.EditorOf(path)),
            StringComparison.OrdinalIgnoreCase
        );
    }

    private void Show()
    {
        if (Exports.Running is { } running)
        {
            Navigation.NavigateTo(Clips.EditorOf(running.SourcePath));
        }
    }

    public void Dispose()
    {
        Exports.Changed -= OnChanged;
        Exports.Progressed -= OnChanged;
        Exports.Finished -= OnFinished;
        GC.SuppressFinalize(this);
    }
}
