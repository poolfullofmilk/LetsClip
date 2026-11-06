using System.Runtime.InteropServices;
using LetsClip.Services.Capture;

namespace LetsClip.Desktop.Interop;

public static partial class SaveChime
{
    // Played Straight From Memory, Without Waiting Or A Fallback Beep
    private const uint PlayAsync = 0x1;
    private const uint NoDefault = 0x2;
    private const uint FromMemory = 0x4;

    // Two Soft Bell Notes, A Fifth Apart
    private const int SampleRate = 48000;
    private const double Length = 0.34;

    // Full Volume In Settings Plays At This Share
    private const double Loudness = 0.16;
    private static readonly (double Frequency, double Delay)[] s_notes =
    [
        (1046.5, 0),
        (1568, 0.07),
    ];

    // The Chime Starts A Moment Late, Its Mute Covers That
    private static readonly TimeSpan s_muted = TimeSpan.FromSeconds(Length + 0.4);

    // One Pinned Wave Per Tenth, Kept While Windows Reads It
    private static readonly byte[]?[] s_waves = new byte[11][];

    public static void Play(ReplayRecorder recorder, double volume)
    {
        // The Loopback Hears Silence, So No Clip Holds The Chime
        recorder.MuteSystemAudio(s_muted);
        var level = (int)Math.Round(Math.Clamp(volume, 0, 1) * 10);
        var wave =
            Volatile.Read(ref s_waves[level])
            ?? Interlocked.CompareExchange(ref s_waves[level], Render(level / 10.0), null)
            ?? s_waves[level]!;

        // Safe From Any Thread, No Output Device Plays Nothing
        _ = PlaySoundW(
            Marshal.UnsafeAddrOfPinnedArrayElement(wave, 0),
            0,
            FromMemory | PlayAsync | NoDefault
        );
    }

    private static byte[] Render(double volume)
    {
        var frames = (int)(SampleRate * Length);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // A Plain 16 Bit Mono WAV Header
        writer.Write("RIFF"u8);
        writer.Write(36 + (frames * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double)SampleRate;
            var sample = 0.0;
            foreach (var (frequency, delay) in s_notes)
            {
                var local = time - delay;
                if (local < 0)
                {
                    continue;
                }

                // A Quick Rise, Then A Bell's Fade
                var envelope = Math.Min(1, local / 0.004) * Math.Exp(-local / 0.085);
                sample +=
                    envelope
                    * (
                        Math.Sin(2 * Math.PI * frequency * local)
                        + (0.25 * Math.Sin(4 * Math.PI * frequency * local))
                    );
            }

            // The Last 30 ms Fade Out, Ending Without A Click
            var fade = Math.Min(1, (Length - time) / 0.03);
            writer.Write(
                (short)(Math.Clamp(sample * Loudness * volume * fade, -1, 1) * short.MaxValue)
            );
        }

        writer.Flush();
        var wave = GC.AllocateArray<byte>((int)stream.Length, pinned: true);
        stream.GetBuffer().AsSpan(0, wave.Length).CopyTo(wave);
        return wave;
    }

    [LibraryImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(nint sound, nint module, uint flags);
}
