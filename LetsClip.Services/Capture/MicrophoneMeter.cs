using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LetsClip.Services.Media;
using LetsClip.Services.Storage;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NLog;

namespace LetsClip.Services.Capture;

public sealed class MicrophoneMeter : IDisposable
{
    private const int Channels = 2;

    // Short Device Buffers, The Voice Filters Add About 110 ms
    private const int MicrophoneMilliseconds = 20;
    private const int OutputMilliseconds = 30;

    // Clocks Drift, Past This Lag The Voice Catches Up
    private static readonly TimeSpan s_maximumLag = TimeSpan.FromMilliseconds(120);
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _ended = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private Process? _filters;
    private Stream? _toFilters;
    private BufferedWaveProvider? _heard;
    private float _peak;

    // Above -80 dB, A Blocked Microphone Sends Pure Zeros
    public bool HeardSound { get; private set; }

    // Only An Unexpected End, Stopping On Purpose Leaves It Open
    public Task Ended => _ended.Task;

    public MicrophoneMeter(string deviceId, bool noiseCancellation)
    {
        try
        {
            // The Picked Microphone, Or The Default Like Recording Uses
            var microphone =
                AudioDevices.Find(_enumerator, deviceId)
                ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

            // The Windows Output, Where Clips Play In The Editor
            using var output = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _player = new WasapiPlayerBuilder()
                .WithDevice(output)
                .WithSharedMode()
                .WithLatency(OutputMilliseconds)
                .Build();
            var sampleRate = _player.DeviceMixFormat.SampleRate;
            _heard = new BufferedWaveProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels),
                TimeSpan.FromSeconds(1)
            )
            {
                DiscardOnBufferOverflow = true,
            };
            _player.PlaybackStopped += (_, _) => _ended.TrySetResult();
            _player.Init(_heard);
            _filters = FFmpeg.Start(
                FFmpeg.FFmpegPath,
                ArgumentsFor(noiseCancellation, sampleRate),
                redirectInput: true
            );
            _toFilters = _filters.StandardInput.BaseStream;
            _ = PassOnAsync(_filters);
            _ = _filters.StandardError.ReadToEndAsync();
            _recorder = new WasapiRecorderBuilder()
                .WithDevice(microphone)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, Channels))
                .WithBufferLength(MicrophoneMilliseconds)
                .Build();
            _recorder.DataAvailable += OnData;
            _recorder.RecordingStopped += (_, _) => _ended.TrySetResult();
            _recorder.StartRecording();
            _player.Play();
            s_logger.Info(
                $"Started Testing Microphone {microphone.FriendlyName} On {output.FriendlyName}"
            );
        }
        catch
        {
            StopAll();
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

    private static List<string> ArgumentsFor(bool noiseCancellation, int sampleRate)
    {
        // The Filters A Clip Gets, As Loud As One Opens
        var volume = UserSettings.OpeningVolume.ToString(CultureInfo.InvariantCulture);
        var cleaning = noiseCancellation ? $"{CaptureSession.VoiceCleaning}," : "";
        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "f32le",
            "-ar",
            AudioMixer.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac",
            "2",
            "-ch_layout",
            "stereo",
            "-i",
            "pipe:0",
            "-af",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{cleaning}volume={volume},aresample={sampleRate}"
            ),
            "-f",
            "f32le",
            "pipe:1",
        ];
    }

    private async Task PassOnAsync(Process filters)
    {
        var chunk = new byte[4096];
        var output = filters.StandardOutput.BaseStream;
        try
        {
            while (await output.ReadAsync(chunk) is > 0 and var count && _heard is { } heard)
            {
                if (heard.BufferedDuration > s_maximumLag)
                {
                    heard.ClearBuffer();
                }

                heard.AddSamples(chunk, 0, count);
            }
        }
        catch (Exception exception)
            when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Stopping Closes The Pipe Under This Read
        }

        _ended.TrySetResult();
    }

    private void OnData(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long performanceCounter
    )
    {
        var silent = flags.HasFlag(AudioClientBufferFlags.Silent);
        if (!silent)
        {
            Measure(MemoryMarshal.Cast<byte, float>(buffer));
        }

        try
        {
            _toFilters?.Write(silent ? new byte[buffer.Length] : buffer);
            _toFilters?.Flush();
        }
        catch (Exception exception)
            when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The Filters Stopped, Ended Tells The Page
        }
    }

    private void Measure(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        lock (_gate)
        {
            _peak = Math.Max(_peak, peak);
            HeardSound |= _peak > 1e-4f;
        }
    }

    private void StopAll()
    {
        // Filters First, A Blocked Pipe Write Then Frees Everything
        if (_filters is { } running)
        {
            FFmpeg.Kill(running);
        }

        try
        {
            if (_recorder is { } recorder)
            {
                recorder.DataAvailable -= OnData;
                recorder.Dispose();
            }

            _player?.Dispose();
        }
        catch (COMException)
        {
            // A Vanished Device Can Fail Its Own Teardown
        }

        _filters?.Dispose();
        (_recorder, _player, _filters, _toFilters, _heard) = (null, null, null, null, null);
    }

    public void Dispose()
    {
        StopAll();
        _enumerator.Dispose();
        s_logger.Info("Stopped Testing Microphone");
        GC.SuppressFinalize(this);
    }
}
