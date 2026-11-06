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

    public static string FormatDateTime(DateTimeOffset time, string clock = "HH:mm")
    {
        // 5th of November 2026 at 21:10, File Names Take 21h10m
        var local = time.ToLocalTime();
        var suffix = local.Day is 11 or 12 or 13
            ? "th"
            : (local.Day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };
        return $"{local.Day}{suffix} of {local.ToString("MMMM yyyy", s_invariant)} at {local.ToString(clock, s_invariant)}";
    }

    public static string FormatMegabytes(double megabytes)
    {
        return megabytes.ToString("0.#", Shown) + " MB";
    }

    private static bool SelfTestPasses()
    {
        var evening = new DateTimeOffset(new DateTime(2026, 11, 4, 22, 43, 5, DateTimeKind.Local));
        var morning = new DateTimeOffset(new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Local));
        return FormatDateTime(evening) == "4th of November 2026 at 22:43"
            && FormatDateTime(morning) == "5th of November 2026 at 08:00"
            && FormatDateTime(evening, "HH'h'mm'm'") == "4th of November 2026 at 22h43m"
            && FormatDateTime(morning.AddDays(-4)) == "1st of November 2026 at 08:00"
            && FormatDateTime(morning.AddDays(6)) == "11th of November 2026 at 08:00"
            && FormatDateTime(morning.AddDays(17)) == "22nd of November 2026 at 08:00"
            && FormatDateTime(morning.AddDays(-2)) == "3rd of November 2026 at 08:00"
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
