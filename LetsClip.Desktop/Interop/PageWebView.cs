using Microsoft.Web.WebView2.Wpf;

namespace LetsClip.Desktop.Interop;

public sealed class PageWebView : WebView2
{
    protected override void Dispose(bool disposing)
    {
        // Blazor Disposes From A Pool Thread, WebView2 Needs Its Own
        if (disposing && !Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(Dispose);
            return;
        }

        base.Dispose(disposing);
    }
}
