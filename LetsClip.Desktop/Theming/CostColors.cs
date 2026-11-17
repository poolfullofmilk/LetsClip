using LetsClip.Data.Enums;
using MudBlazor;

namespace LetsClip.Desktop.Theming;

public static class CostColors
{
    // Heavier Picks Read Amber, The Heaviest Red
    public static Color Replay(int seconds)
    {
        return seconds >= 600 ? Color.Error
            : seconds >= 300 ? Color.Warning
            : Color.Default;
    }

    public static Color Resolution(int lines)
    {
        return lines > 1440 ? Color.Error
            : lines > 1080 ? Color.Warning
            : Color.Default;
    }

    public static Color FrameRate(int rate)
    {
        return rate >= 165 ? Color.Error
            : rate >= 120 ? Color.Warning
            : Color.Default;
    }

    public static Color Quality(CaptureQuality quality)
    {
        return quality switch
        {
            CaptureQuality.Max => Color.Error,
            CaptureQuality.High => Color.Warning,
            _ => Color.Default,
        };
    }
}
