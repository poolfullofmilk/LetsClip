using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LetsClip.Data.Helpers;

public static partial class ClipNames
{
    // Saved Names Keep The Sortable Date
    private const string DateFormat = "yyyy-MM-dd HH-mm-ss";

    // Long Enough To Read, Short Enough For Any Folder
    private const int MaximumNameLength = 120;

    static ClipNames()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static string For(DateTimeOffset recordedAt)
    {
        return $"{AppInfo.DisplayName} {recordedAt.ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture)}";
    }

    public static DateTimeOffset? DateOf(string name)
    {
        // Saved Names Read As Their Date, Renamed Ones Give None
        var match = GeneratedName().Match(name);
        return
            match.Success
            && DateTime.TryParseExact(
                match.Groups["stamp"].Value,
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var stamp
            )
            ? new DateTimeOffset(stamp)
            : null;
    }

    public static string? FileSafe(string? title)
    {
        // A Typed Title As A File Name, Refused Characters Dropped
        var cleaned = string.Concat(
                (title ?? "").Where(character =>
                    !Path.GetInvalidFileNameChars().Contains(character)
                )
            )
            .Trim()
            .TrimEnd('.');
        if (cleaned.Length > MaximumNameLength)
        {
            var cut = char.IsHighSurrogate(cleaned[MaximumNameLength - 1]) ? 1 : 0;
            cleaned = cleaned[..(MaximumNameLength - cut)].TrimEnd(' ', '.');
        }

        // Windows Keeps Device Names For Itself
        return cleaned.Length == 0 ? null
            : ReservedName().IsMatch(cleaned) ? $"Clip {cleaned}"
            : cleaned;
    }

    [GeneratedRegex(
        @"^" + AppInfo.DisplayName + @" (?<stamp>\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})( \(\d+\))?$"
    )]
    private static partial Regex GeneratedName();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    private static bool SelfTestPasses()
    {
        var evening = new DateTimeOffset(new DateTime(2026, 11, 4, 22, 43, 5, DateTimeKind.Local));
        return For(evening) == "LetsClip 2026-11-04 22-43-05"
            && DateOf(For(evening)) == evening
            && DateOf("LetsClip 2026-11-04 22-43-05 (2)") == evening
            && DateOf("LetsClip 2026-11-04 22-43-05 Ace") is null
            && DateOf("Ace Clutch") is null
            && DateOf("LetsClip 2026-13-04 22-43-05") is null
            && FileSafe(" Ace: 1v5? ") == "Ace 1v5"
            && FileSafe("Clutch...") == "Clutch"
            && FileSafe("<>|") is null
            && FileSafe(null) is null
            && FileSafe("CON") == "Clip CON"
            && FileSafe("nul.txt") == "Clip nul.txt"
            && FileSafe("lpt9") == "Clip lpt9"
            && FileSafe("Console") == "Console"
            && FileSafe(new string('x', 400))?.Length == MaximumNameLength;
    }
}
