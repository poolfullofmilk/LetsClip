using LetsClip.Data.Helpers;

namespace LetsClip.Data.ViewModels;

public sealed record SoundMix(
    bool Game = true,
    bool Microphone = true,
    bool Voice = true,
    double GameVolume = 1,
    double MicrophoneVolume = 1,
    double VoiceVolume = 1
)
{
    public bool Keeps(string stem)
    {
        return stem switch
        {
            AudioTracks.Game => Game,
            AudioTracks.Microphone => Microphone,
            AudioTracks.Voice => Voice,
            _ => false,
        };
    }

    public double LevelOf(string stem)
    {
        return stem switch
        {
            AudioTracks.Game => GameVolume,
            AudioTracks.Microphone => MicrophoneVolume,
            AudioTracks.Voice => VoiceVolume,
            _ => 1,
        };
    }
}
