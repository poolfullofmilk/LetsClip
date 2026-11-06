using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NLog;

namespace LetsClip.Services.Capture;

public sealed class MicrophoneMeter : IDisposable
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly WasapiRecorder _recorder;
    private readonly Lock _gate = new();
    private float _peak;

    // Above -80 dB, A Blocked Microphone Sends Pure Zeros
    public bool HeardSound { get; private set; }

    public MicrophoneMeter(string deviceId)
    {
        try
        {
            // The Picked Microphone, Or The Default Like Recording Uses
            var device =
                AudioDevices.Find(_enumerator, deviceId)
                ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            _recorder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, 2))
                .WithBufferLength(100)
                .Build();
            _recorder.DataAvailable += OnData;
            _recorder.StartRecording();
            s_logger.Info($"Started Testing Microphone {device.FriendlyName}");
        }
        catch
        {
            _enumerator.Dispose();
            throw;
        }
    }

    public double ReadLevel()
    {
        // The Loudest Sample Since The Last Reading, Top 60 dB
        float peak;
        lock (_gate)
        {
            peak = _peak;
            _peak = 0;
        }

        return Math.Clamp(1 + (20 * Math.Log10(Math.Max(peak, 1e-6)) / 60), 0, 1);
    }

    private void OnData(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long performanceCounter
    )
    {
        var peak = 0f;
        foreach (var sample in MemoryMarshal.Cast<byte, float>(buffer))
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        lock (_gate)
        {
            _peak = Math.Max(_peak, flags.HasFlag(AudioClientBufferFlags.Silent) ? 0 : peak);
            HeardSound |= _peak > 1e-4f;
        }
    }

    public void Dispose()
    {
        _recorder.DataAvailable -= OnData;
        try
        {
            _recorder.Dispose();
        }
        catch (COMException)
        {
            // A Vanished Device Can Fail Its Own Teardown
        }

        _enumerator.Dispose();
        s_logger.Info("Stopped Testing Microphone");
        GC.SuppressFinalize(this);
    }
}
