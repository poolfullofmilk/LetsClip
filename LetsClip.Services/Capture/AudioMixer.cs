using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LetsClip.Services.Games;
using NAudio.CoreAudioApi;
using NLog;

namespace LetsClip.Services.Capture;

public sealed partial class AudioMixer : IDisposable
{
    // Stereo Float At 48 kHz, The Encoder Reads It Raw
    public const int SampleRate = 48000;
    private const int Channels = 2;

    // Writes Trail Real Time So Late Packets Still Land
    private const int TrailFrames = SampleRate * 300 / 1000;

    // Windows Keeps A Second While A Capture Thread Waits
    private const int BufferMilliseconds = 1000;

    // Capture Threads Run In Windows' Audio Class, Never Starved
    private const string ThreadClass = "Audio";

    // 40 ms Chunks, Fewer Wakeups Keep The CPU Asleep
    private const int ChunkFrames = SampleRate / 25;

    // E_NOTFOUND, Windows Has No Device Of That Kind At All
    private const int NoDevice = unchecked((int)0x80070490);

    // A Voice App Started Or Closed Is Noticed Within This
    private static readonly TimeSpan s_voiceCheckPeriod = TimeSpan.FromSeconds(10);
    private const string VoiceKind = "Voice Chat Audio";
    private const string HelperKind = "Game Helper Audio";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    private readonly long _origin;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly int _outputChannels;

    // Replaced Whole, The Writer Reads One List Per Chunk
    private volatile AudioSource[] _sources = [];
    private readonly Lock _sourcesGate = new();
    private readonly Timer? _voiceWatch;

    // The Game And Its Starter, Both Trees Hold It
    private readonly int[] _holdingGame = [];

    // A Voice App That Started The Game Records Both
    private readonly int _game;
    private readonly bool _throughStarter;
    private Thread? _writer;
    private volatile bool _stopping;
    private long _emittedFrames;
    private long _muteUntilFrame = long.MinValue;

    // Halved So It Passes The Full Scale Clamp
    private volatile float _halfMicrophoneVolume;

    public AudioMixer(
        long originTimestamp,
        string? systemId,
        string? microphoneId,
        bool separateMicrophone,
        double microphoneVolume,
        int gameProcess
    )
    {
        // A Cleaned Microphone Gets Two Channels, Its Volume A Fifth
        _origin = originTimestamp;
        _outputChannels = separateMicrophone ? (Channels * 2) + 1 : Channels;
        SetMicrophoneVolume(microphoneVolume);
        if (systemId is not null && gameProcess != 0)
        {
            // Only The Game And Voice Chat, Each With Its Children
            var starter = GameWatcher.StarterOf(gameProcess);
            _game = gameProcess;
            _holdingGame = [gameProcess, starter];
            _throughStarter = starter != 0 && GameWatcher.VoiceProcesses().Contains(starter);
            _sources =
            [
                new AudioSource(
                    this,
                    "Game Audio",
                    "",
                    true,
                    0,
                    _throughStarter ? starter : gameProcess
                ),
            ];
        }
        else if (systemId is not null)
        {
            _sources = [new AudioSource(this, "System Audio", systemId, true, 0)];
        }

        if (microphoneId is not null)
        {
            _sources =
            [
                .. _sources,
                new AudioSource(
                    this,
                    "Microphone",
                    microphoneId,
                    false,
                    separateMicrophone ? Channels : 0
                ),
            ];
        }

        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DefaultDeviceChanged += (_, eventArgs) =>
            Reconnect(source =>
                source.FollowsDefault
                && eventArgs.Flow == source.Flow
                && eventArgs.Role == Role.Console
            );
        _notifications.DeviceStateChanged += (_, eventArgs) =>
            Reconnect(source =>
                eventArgs.NewState == DeviceState.Active && source.Wants(eventArgs.DeviceId)
            );
        _notifications.DeviceAdded += (_, eventArgs) =>
            Reconnect(source => source.Wants(eventArgs.DeviceId));
        foreach (var source in _sources)
        {
            // A Slow Device Joins Later, Capture Starts Without It
            _ = Task.Run(source.Connect);
        }

        if (systemId is not null && gameProcess != 0)
        {
            // Its Tree Holds The Game Once, Apart It Doubled
            if (_throughStarter)
            {
                s_logger.Info(
                    $"Recorded {VoiceKind} Of Process {_holdingGame[1]} With The Game It Started, The Game's Own Helpers Apart"
                );
            }

            _voiceWatch = new Timer(_ => FollowVoiceApps());
            _voiceWatch.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    // Windows 10 2004 And Later Record One Process Tree
    [SupportedOSPlatformGuard("windows10.0.19041")]
    public static bool CanRecordProcesses => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public string? Problem =>
        _sources.Select(source => source.Problem).FirstOrDefault(problem => problem is not null);

    private void FollowVoiceApps()
    {
        // Voice Apps And Game Helpers Get Recorders, Closed Ones Go
        try
        {
            var voices = GameWatcher.VoiceProcesses().Except(_holdingGame).ToList();
            var helpers = _throughStarter ? GameWatcher.ChildrenOf(_game) : [];
            lock (_sourcesGate)
            {
                Follow(VoiceKind, voices);
                Follow(HelperKind, helpers);
            }
        }
        catch (Exception exception)
        {
            // A Throwing Timer Ends LetsClip, The Game Still Records
            s_logger.Debug($"Failed To Look For Voice Apps {exception.GetType().Name}");
        }
        finally
        {
            try
            {
                if (!_stopping)
                {
                    _voiceWatch?.Change(s_voiceCheckPeriod, Timeout.InfiniteTimeSpan);
                }
            }
            catch (ObjectDisposedException)
            {
                // The Session Ended Meanwhile
            }
        }
    }

    private void Follow(string kind, IReadOnlyList<int> running)
    {
        // Swapped Whole, The Writer Reads One List Per Chunk
        foreach (
            var gone in _sources.Where(source =>
                source.Kind == kind && !running.Contains(source.ProcessId)
            )
        )
        {
            _sources = [.. _sources.Where(source => source != gone)];
            gone.Dispose();
        }

        foreach (var process in running)
        {
            if (_stopping || _sources.Any(source => source.ProcessId == process))
            {
                continue;
            }

            var source = new AudioSource(this, kind, "", true, 0, process);
            source.Connect();
            _sources = [.. _sources, source];
        }
    }

    public void Attach(Stream output)
    {
        _writer = new Thread(() => WriteLoop(output))
        {
            IsBackground = true,
            Name = "LetsClip Audio Writer",
            Priority = ThreadPriority.AboveNormal,
        };
        _writer.Start();
    }

    public void SetMicrophoneVolume(double volume)
    {
        // The Encoder Multiplies The Cleaned Microphone By It, No Restart
        _halfMicrophoneVolume = (float)(volume / 2);
    }

    public void MuteSystemAudio(TimeSpan duration)
    {
        // Our Own Save Chime Never Lands In The Next Clip
        Interlocked.Exchange(
            ref _muteUntilFrame,
            CurrentFrame() + (long)(duration.TotalSeconds * SampleRate)
        );
    }

    private long CurrentFrame()
    {
        return (long)(
            (Stopwatch.GetTimestamp() - _origin) * (double)SampleRate / Stopwatch.Frequency
        );
    }

    private long FrameOfPerformanceCounter(long hundredNanoseconds)
    {
        // WASAPI Hands Its QPC Time In 100 ns Units
        var originHundreds = _origin * 10_000_000.0 / Stopwatch.Frequency;
        return (long)((hundredNanoseconds - originHundreds) * SampleRate / 10_000_000);
    }

    private void Reconnect(Func<AudioSource, bool> matches)
    {
        // Notifications Run On The Audio Thread, WASAPI Calls Deadlock There
        var targets = _sources.Where(matches).ToList();
        if (targets.Count == 0 || _stopping)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (var source in targets)
            {
                source.Connect();
            }
        });
    }

    private void WriteLoop(Stream output)
    {
        var mix = new float[ChunkFrames * _outputChannels];
        try
        {
            while (!_stopping)
            {
                if (_emittedFrames + ChunkFrames > CurrentFrame() - TrailFrames)
                {
                    Thread.Sleep(20);
                    continue;
                }

                Array.Clear(mix);
                foreach (var source in _sources)
                {
                    source.Drain(_emittedFrames, mix, _outputChannels);
                }

                if (_outputChannels > Channels * 2)
                {
                    var volume = _halfMicrophoneVolume;
                    for (var index = Channels * 2; index < mix.Length; index += _outputChannels)
                    {
                        mix[index] = volume;
                    }
                }

                for (var index = 0; index < mix.Length; index++)
                {
                    mix[index] = Math.Clamp(mix[index], -1f, 1f);
                }

                output.Write(MemoryMarshal.AsBytes(mix.AsSpan()));
                _emittedFrames += ChunkFrames;
                foreach (var source in _sources)
                {
                    source.ReportLate(closing: false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The Encoder Closed Its Input, The Recorder Reports Why
        }
        catch (Exception exception)
        {
            // A Throwing Thread Ends LetsClip, The Stall Check Restarts
            s_logger.Warn($"Failed To Write Audio {exception.GetType().Name}");
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _voiceWatch?.Dispose();
        _writer?.Join(TimeSpan.FromSeconds(2));
        _notifications.Dispose();
        lock (_sourcesGate)
        {
            foreach (var source in _sources)
            {
                source.Dispose();
            }
        }

        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
