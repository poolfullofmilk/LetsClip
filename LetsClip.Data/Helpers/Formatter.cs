using System.Diagnostics;
using System.Globalization;

namespace LetsClip.Data.Helpers;

public static class Formatter
{
    private static readonly CultureInfo s_invariant = CultureInfo.InvariantCulture;

    // One Date Format Everywhere, Day First
    private const string DisplayDateTimeFormat = "dd/MM/yyyy HH'h'mm";

    // File Names Keep The Sortable Date
    private const string FileDateTimeFormat = "yyyy-MM-dd HH-mm-ss";

    static Formatter()
    {
        Debug.Assert(SelfTestPasses());
    }

    // Shown Numbers Follow The Windows Format
    private static CultureInfo Shown => CultureInfo.CurrentCulture;

    private static string Point => Shown.NumberFormat.NumberDecimalSeparator;

    public static string ClockPattern => $"m:ss{Point}s";

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
        // The Two Largest Parts, 3m 12s Or 2h 5m
        var whole = (long)Math.Round(Math.Max(0, seconds));
        if (whole < 60)
        {
            return $"{whole}s";
        }

        return whole < 3600
            ? $"{whole / 60}m {whole % 60}s"
            : $"{whole / 3600}h {whole % 3600 / 60}m";
    }

    public static string FormatClock(double seconds)
    {
        // Minutes, Seconds And Tenths, 1:05.3
        var tenths = (long)Math.Round(Math.Max(0, seconds) * 10);
        var minutes = tenths / 600;
        var rest = tenths % 600;
        return string.Create(s_invariant, $"{minutes}:{rest / 10:00}{Point}{rest % 10}");
    }

    public static double? ParseClock(string? text)
    {
        // Reads 65.3, 1:05.3 And 1:05, A Decimal Comma Too
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Replace(',', '.').Split(':');
        if (
            parts.Length > 2
            || !double.TryParse(
                parts[^1],
                NumberStyles.AllowDecimalPoint,
                s_invariant,
                out var seconds
            )
        )
        {
            return null;
        }

        if (parts.Length == 1)
        {
            return seconds;
        }

        return
            int.TryParse(parts[0], NumberStyles.None, s_invariant, out var minutes) && seconds < 60
            ? minutes * 60 + seconds
            : null;
    }

    public static string FormatDateTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString(DisplayDateTimeFormat, s_invariant);
    }

    public static string FormatFileDateTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString(FileDateTimeFormat, s_invariant);
    }

    public static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.0", Shown) + "s";
    }

    public static string FormatMegabytes(double megabytes)
    {
        return megabytes.ToString("0.#", Shown) + " MB";
    }

    private static bool SelfTestPasses()
    {
        var evening = new DateTimeOffset(new DateTime(2026, 11, 4, 22, 43, 5, DateTimeKind.Local));
        return FormatDateTime(evening) == "04/11/2026 22h43"
            && FormatFileDateTime(evening) == "2026-11-04 22-43-05"
            && FormatSize(20_000_000) == $"20{Point}0 MB"
            && FormatSize(999_499) == "999 KB"
            && FormatSize(1_250_000_000) == $"1{Point}25 GB"
            && FormatDuration(59.6) == "1m 0s"
            && FormatDuration(192) == "3m 12s"
            && FormatDuration(7500) == "2h 5m"
            && FormatClock(65.34) == $"1:05{Point}3"
            && FormatClock(59.96) == $"1:00{Point}0"
            && ParseClock("1:05.3") is > 65.29 and < 65.31
            && ParseClock("1:05,3") is > 65.29 and < 65.31
            && ParseClock("65.3") is > 65.29 and < 65.31
            && ParseClock("1:75") is null
            && ParseClock("-2") is null
            && ParseClock("a:b") is null;
    }
}
