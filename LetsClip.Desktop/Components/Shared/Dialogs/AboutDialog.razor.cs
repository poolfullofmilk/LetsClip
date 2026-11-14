using LetsClip.Desktop.Interop;
using LetsClip.Services.Capture;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace LetsClip.Desktop.Components.Shared.Dialogs;

public partial class AboutDialog
{
    [CascadingParameter]
    public required IMudDialogInstance MudDialog { get; set; }

    [Inject]
    public required CaptureController Controller { get; set; }

    // What Records Now Or Last, Graphics Card Or CPU
    private string? EncoderText =>
        Controller.Recorder.Encoder is { } encoder
            ? $"Records With {encoder.Name}{(encoder.GpuFrames ? " On The GPU" : "")}"
            : null;

    private static void OpenNotices()
    {
        // The Single File Unpacks Notices Beside The App
        Explorer.Open(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
    }
}
