using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LetsClip.Data.Helpers;

public static partial class ClipNames
{
    static ClipNames()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static string For(DateTimeOffset recordedAt)
    {
        return $"{AppInfo.DisplayName} {Formatter.FormatFileDateTime(recordedAt)}";
    }

    public static bool IsGenerated(string name)
    {
        // Saved Names Read As Their Date, Renamed Ones As Typed
        return GeneratedName().IsMatch(name);
    }

    [GeneratedRegex(
        @"^" + AppInfo.DisplayName + @" \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(\d+\))?$"
    )]
    private static partial Regex GeneratedName();

    private static bool SelfTestPasses()
    {
        var evening = new DateTimeOffset(new DateTime(2026, 11, 4, 22, 43, 5, DateTimeKind.Local));
        return For(evening) == "LetsClip 2026-11-04 22-43-05"
            && IsGenerated(For(evening))
            && IsGenerated("LetsClip 2026-11-04 22-43-05 (2)")
            && !IsGenerated("LetsClip 2026-11-04 22-43-05 Ace")
            && !IsGenerated("Ace Clutch");
    }
}
