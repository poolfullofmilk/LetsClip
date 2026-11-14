namespace LetsClip.Data.Helpers;

public static class AudioTracks
{
    // A Clip With A Microphone Carries Three, The Mix First
    public const string Mix = "Game and Microphone";
    public const string Game = "Game";
    public const string Microphone = "Microphone";

    public static readonly string[] Separate = [Mix, Game, Microphone];

    public static int? IndexOf(bool game, bool microphone)
    {
        // Both Keep The Mix, One Its Own Track, Neither None
        return (game, microphone) switch
        {
            (true, true) => 0,
            (true, false) => 1,
            (false, true) => 2,
            _ => null,
        };
    }
}
