using Microsoft.JSInterop;

namespace LetsClip.Desktop.Interop;

public static class PageScript
{
    public static async Task CallAsync(
        this IJSRuntime javaScript,
        string function,
        params object?[] arguments
    )
    {
        try
        {
            await javaScript.InvokeVoidAsync(function, arguments);
        }
        catch (JSDisconnectedException)
        {
            // The Window Is Closing, Its Page Goes With It
        }
    }
}
