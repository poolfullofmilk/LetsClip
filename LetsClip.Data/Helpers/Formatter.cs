using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LetsClip.Data.Helpers;

public static partial class Formatter
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
        // Decimal Units Like The Size Target, Never A ,0
        return bytes switch
        {
            >= 1_000_000_000 => (bytes / 1e9).ToString("0.##", Shown) + " GB",
            >= 1_000_000 => (bytes / 1e6).ToString("0.#", Shown) + " MB",
            _ => Math.Max(1, (long)Math.Round(bytes / 1e3)).ToString(Shown) + " KB",
        };
    }

    public static string FormatDuration(double seconds)
    {
        // Zero Parts Left Out, 8m30,5s Or 1h2m
        var tenths = (long)TenthsOf(seconds);
        var hours = tenths / 36000;
        var minutes = tenths / 600 % 60;
        var rest = tenths % 600 / 10.0;
        var text = (hours > 0 ? $"{hours}h" : "") + (minutes > 0 ? $"{minutes}m" : "");
        return rest > 0 || text.Length == 0 ? text + rest.ToString("0.#", Shown) + "s" : text;
    }

    public static string FormatSeconds(double seconds)
    {
        // Plain Seconds To A Tenth, 90,5 For 1m30,5s
        return (TenthsOf(seconds) / 10).ToString("0.#", Shown);
    }

    private static double TenthsOf(double seconds)
    {
        // Halves Go Up, The Page Script Rounds The Same
        return Math.Round(Math.Max(0, seconds) * 10, MidpointRounding.AwayFromZero);
    }

    public static double? ParseDuration(string? text)
    {
        // The Shown Form Or Plain Seconds Read Back
        var match = DurationText()
            .Match((text ?? "").Replace(Point, ".").Replace(" ", "").ToLowerInvariant());
        if (!match.Success || match.Length == 0)
        {
            return null;
        }

        double Part(string name) =>
            match.Groups[name].Success
                ? double.Parse(match.Groups[name].Value.Replace(',', '.'), s_invariant)
                : 0;
        return TenthsOf((Part("hours") * 3600) + (Part("minutes") * 60) + Part("seconds")) / 10;
    }

    [GeneratedRegex(
        @"^(?:(?<hours>\d+)h)?(?:(?<minutes>\d+)m)?(?:(?<seconds>\d+(?:[.,]\d*)?)s?)?$"
    )]
    private static partial Regex DurationText();

    public static string FormatDay(DateTimeOffset time, DateTime? today = null)
    {
        // Today And Yesterday By Name, Older Days As 6-11-2026
        var local = time.ToLocalTime();
        return ((today ?? DateTime.Today) - local.Date).Days switch
        {
            0 => "Today",
            1 => "Yesterday",
            _ => local.ToString("d-M-yyyy", s_invariant),
        };
    }

    public static string FormatTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString("H:mm", s_invariant);
    }

    public static string FormatDateTime(DateTimeOffset time, DateTime? today = null)
    {
        return $"{FormatDay(time, today)} at {FormatTime(time)}";
    }

    public static string FormatFileDate(DateTimeOffset time)
    {
        // Full Date, 8h42 Since A Name Holds No Colon
        return time.ToLocalTime().ToString("d-M-yyyy 'at' H'h'mm", s_invariant);
    }

    public static string FormatResolution(int width, int height)
    {
        return $"{width} × {height}";
    }

    public static string FormatFrameRate(double rate)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{rate:0} FPS");
    }

    public static string FormatBitrate(double bitsPerSecond)
    {
        // Whole Kilobits Below A Megabit
        return bitsPerSecond >= 1_000_000
            ? (bitsPerSecond / 1e6).ToString("0.#", Shown) + " Mbps"
            : Math.Max(1, (long)Math.Round(bitsPerSecond / 1e3)).ToString(Shown) + " kbps";
    }

    private static bool SelfTestPasses()
    {
        var evening = new DateTimeOffset(new DateTime(2026, 11, 4, 22, 43, 5, DateTimeKind.Local));
        var morning = new DateTimeOffset(new DateTime(2026, 11, 6, 8, 5, 0, DateTimeKind.Local));
        var today = new DateTime(2026, 11, 6);
        return FormatDateTime(morning, today) == "Today at 8:05"
            && FormatDateTime(morning.AddHours(-9), today) == "Yesterday at 23:05"
            && FormatDateTime(evening, today) == "4-11-2026 at 22:43"
            && FormatDateTime(morning.AddDays(1), today) == "7-11-2026 at 8:05"
            && FormatFileDate(morning) == "6-11-2026 at 8h05"
            && FormatFileDate(evening) == "4-11-2026 at 22h43"
            && FormatSize(20_000_000) == "20 MB"
            && FormatSize(20_540_000) == $"20{Point}5 MB"
            && FormatSize(2_000_000_000) == "2 GB"
            && FormatSize(999_499) == "999 KB"
            && FormatSize(1_250_000_000) == $"1{Point}25 GB"
            && FormatBitrate(11_570_000) == $"11{Point}6 Mbps"
            && FormatBitrate(30_010_000) == "30 Mbps"
            && FormatBitrate(1_000_000) == "1 Mbps"
            && FormatBitrate(122_400) == "122 kbps"
            && FormatBitrate(70) == "1 kbps"
            && FormatResolution(1920, 1080) == "1920 × 1080"
            && FormatFrameRate(59.94) == "60 FPS"
            && FormatDuration(0) == "0s"
            && FormatDuration(0.25) == $"0{Point}3s"
            && FormatSeconds(0.25) == $"0{Point}3"
            && ParseDuration("0.25") == 0.3
            && FormatDuration(30.3) == $"30{Point}3s"
            && FormatDuration(510.54) == $"8m30{Point}5s"
            && FormatDuration(59.96) == "1m"
            && FormatDuration(3725) == "1h2m5s"
            && FormatDuration(7500) == "2h5m"
            && ParseDuration("23,5") == 23.5
            && ParseDuration("23.54s") == 23.5
            && ParseDuration(" 1m 30,5s") == 90.5
            && ParseDuration("1m") == 60
            && ParseDuration("1h2m5s") == 3725
            && ParseDuration(FormatDuration(510.54)) == 510.5
            && FormatSeconds(90.54) == $"90{Point}5"
            && ParseDuration(FormatSeconds(90.54)) == 90.5
            && ParseDuration("") is null
            && ParseDuration("abc") is null
            && ParseDuration("-3") is null;
    }
}
