using System.Diagnostics;

namespace LetsClip.Data.Helpers;

public static class ProcessStarts
{
    public static ProcessStartInfo WithoutWebView(this ProcessStartInfo startInfo)
    {
        // LetsClip's WebView2 Profile And Flags Never Reach Another App
        foreach (
            var name in startInfo
                .Environment.Keys.Where(name =>
                    name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)
                )
                .ToList()
        )
        {
            startInfo.Environment.Remove(name);
        }

        return startInfo;
    }
}
