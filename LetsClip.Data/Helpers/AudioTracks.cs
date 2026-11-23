using System.Diagnostics;

namespace LetsClip.Data.Helpers;

public static class AudioTracks
{
    // Each Part Of The Sound, Beside The Mix Played First
    public const string Game = "Game";
    public const string Microphone = "Microphone";
    public const string Voice = "Voice Chat";

    static AudioTracks()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static string MixOf(IReadOnlyList<string> stems)
    {
        // Named For What It Holds, Like Game And Microphone
        return stems.Count < 2
            ? string.Join("", stems)
            : $"{string.Join(", ", stems.Take(stems.Count - 1))} and {stems[^1]}";
    }

    public static string[] StemsOf(IReadOnlyList<string?> names)
    {
        // LetsClip's Tracks, The Mix First, Named For Its Parts
        var stems = names.Skip(1).OfType<string>().ToArray();
        return
            names.Count > 1
            && stems.Length == names.Count - 1
            && stems.All(stem => stem is Game or Microphone or Voice)
            && stems.Distinct().Count() == stems.Length
            && names[0] == MixOf(stems)
            ? stems
            : [];
    }

    public static string[]? PartsOf(string? name)
    {
        // A Part's Name, Or A Mix Named For Its Parts
        if (name is Game or Microphone or Voice)
        {
            return [name];
        }

        var parts = name?.Replace(" and ", ", ", StringComparison.Ordinal).Split(", ") ?? [];
        return
            parts.Length > 1
            && parts.All(part => part is Game or Microphone or Voice)
            && MixOf(parts) == name
            ? parts
            : null;
    }

    private static bool SelfTestPasses()
    {
        // Older Clips Named Their Mix Game And Microphone
        return StemsOf(["Game and Microphone", Game, Microphone]) is [Game, Microphone]
            && StemsOf([MixOf([Game, Microphone, Voice]), Game, Microphone, Voice]).Length == 3
            && MixOf([Game, Microphone, Voice]) == "Game, Microphone and Voice Chat"
            && MixOf([Game, Voice]) == "Game and Voice Chat"
            && StemsOf(["Game and Microphone", Game]).Length == 0
            && StemsOf(["SoundHandler", null]).Length == 0
            && StemsOf([null]).Length == 0
            && PartsOf("Game and Microphone") is [Game, Microphone]
            && PartsOf(Voice) is [Voice]
            && PartsOf("SoundHandler") is null
            && PartsOf(null) is null;
    }
}
