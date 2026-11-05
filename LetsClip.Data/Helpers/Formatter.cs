using System.Diagnostics;
using System.Globalization;

namespace LetsClip.Data.Helpers;

public static class Formatter
{
    private static readonly CultureInfo s_invariant = CultureInfo.InvariantCulture;

    static Formatter()
    {
        Debug.Assert(SelfTestPasses());
    }

    // Shown Numbers Follow The Windows Format
    private static CultureInfo Shown => CultureInfo.CurrentCulture;

    private static string Point => Shown.NumberFormat.NumberDecimalSeparator;

    public static string FormatSize(long bytes)
    {
        // Decimal Units, The Same Megabyte The Size Target Uses
        return bytes switch
        {
            >= 1_000_000_000 => (bytes / 1e9).ToString("0.00", Shown) + " GB",
            >= 1_000_000 => (bytes / 1e6).ToString("0.0", Shown) + " MB",
            _ => Math.Max(1, (long)Math.Round(bytes / 1e3)).ToString(Shown) + " KB",
        };
    }

    public static string FormatDuration(double seconds)
    {
        // Zero Parts Left Out, 8m30,5s Or 1h2m
        var tenths = (long)Math.Round(Math.Max(0, seconds) * 10);
        var hours = tenths / 36000;
        var minutes = tenths / 600 % 60;
        var rest = tenths % 600 / 10.0;
        var text = (hours > 0 ? $"{hours}h" : "") + (minutes > 0 ? $"{minutes}m" : "");
        return rest > 0 || text.Length == 0 ? text + rest.ToString("0.#", Shown) + "s" : text;
    }

    public static string FormatDateTime(DateTimeOffset time)
    {
        // Day First, No Leading Zero, 05-11-2026 8h30
        var local = time.ToLocalTime();
        var minutes = local.Minute > 0 ? local.Minute.ToString(s_invariant) : "";
        return local.ToString("dd-MM-yyyy H'h'", s_invariant) + minutes;
    }

    public static string FormatMegabytes(double megabytes)
    {
        return megabytes.ToString("0.#", Shown) + " MB";
    }

    private static bool SelfTestPasses()
    {
        var evening = new DateTimeOffset(new DateTime(2026, 11, 4, 22, 43, 5, DateTimeKind.Local));
        var morning = new DateTimeOffset(new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Local));
        return FormatDateTime(evening) == "04-11-2026 22h43"
            && FormatDateTime(morning) == "05-11-2026 8h"
            && FormatDateTime(morning.AddMinutes(5)) == "05-11-2026 8h5"
            && FormatSize(20_000_000) == $"20{Point}0 MB"
            && FormatSize(999_499) == "999 KB"
            && FormatSize(1_250_000_000) == $"1{Point}25 GB"
            && FormatDuration(0) == "0s"
            && FormatDuration(30.3) == $"30{Point}3s"
            && FormatDuration(510.54) == $"8m30{Point}5s"
            && FormatDuration(59.96) == "1m"
            && FormatDuration(3725) == "1h2m5s"
            && FormatDuration(7500) == "2h5m";
    }
}
