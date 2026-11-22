using System.Diagnostics;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Media;
using NLog;

namespace LetsClip.Services.Capture;

public sealed record CaptureOptions(
    MonitorInfo Monitor,
    int Seconds,
    int FrameRate,
    int Resolution,
    CaptureQuality Quality,
    string? SystemAudioId,
    string? MicrophoneId,
    bool CleanMicrophone,
    double MicrophoneVolume,
    nint GameWindow,
    string? GameName,
    int GameProcess,
    double VoiceVolume = 1
)
{
    // Cleaning And Volume Both Filter The Microphone Alone
    public bool SeparatesMicrophone =>
        MicrophoneId is not null && (CleanMicrophone || MicrophoneVolume != 1);
}

public sealed record SaveOutcome(ClipInfo? Clip, string? Error);

public sealed class CaptureBlockedException(string message, CaptureFix fix)
    : InvalidOperationException(message)
{
    public CaptureFix Fix { get; } = fix;
}

public sealed class WindowRefusedException()
    : InvalidOperationException("Windows refused to record the window");

public sealed partial class ReplayRecorder : IDisposable
{
    // Room Beside What Spills, And The Floor That Stops Spilling
    private const long StopFreeBytes = 1_000_000_000;

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan s_saveTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan s_healthPeriod = TimeSpan.FromSeconds(1);

    // A Disk Froze 9 s Once, Only Longer Is Dead
    private static readonly TimeSpan s_stallTimeout = TimeSpan.FromSeconds(20);

    // A Moving Window Closes Its Segment Within This
    private static readonly TimeSpan s_windowWait = TimeSpan.FromSeconds(1.5);

    // A Fresh Capture Of A Moving Window Draws Within This
    private static readonly TimeSpan s_windowProbe = TimeSpan.FromSeconds(5);

    // A Session This Old Had Worked, Its Retry Comes Quick
    private static readonly TimeSpan s_ranLong = TimeSpan.FromSeconds(30);

    // How Long A Fallback Waits Before Trying Better Encoders
    private static readonly TimeSpan[] s_upgradeWaits =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    ];

    // Readings Before A Passing Problem Is Worth Showing
    private const int ProblemTicks = 5;
    private const int SlowTicks = 10;

    private readonly ClipLibrary _library;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Timer _health;
    private CaptureSession? _session;

    // Kept From Start Until Stopped, Its Session Holds Every Segment
    private ManualRecording? _manual;

    // An Ended Session's Replay, Saveable For Its Own Length
    private KeptReplay? _kept;
    private int _saves;
    private int _healthTicks;
    private int _problemTicks;
    private bool _windowQuiet;

    // A Stalled Session Fails Once, Its Shutdown Takes Seconds
    private long _stalledOrigin;

    public ReplayRecorder(ClipLibrary library)
    {
        _library = library;
        Status = CaptureStatus.Idle;
        // The Health Check Only Ticks While A Session Runs
        _health = new Timer(
            _ => CheckHealth(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    public event Action? StatusChanged;

    // Raised The Moment A Save Is Accepted, Before Any Waiting
    public event Action? SaveStarted;

    public event Action<ClipInfo>? ClipSaved;

    public event Action<CaptureFailure>? Failed;

    // A Recording Ended By Itself And Was Not Saved
    public event Action<string>? RecordingNotSaved;

    public CaptureStatus Status { get; private set; }

    // Set While A Recording Runs Until Stopped
    public DateTimeOffset? RecordingSince
    {
        get
        {
            lock (_gate)
            {
                return _manual?.StartedAt;
            }
        }
    }

    // What A Save Gives Now, Growing Until Full
    public double HeldSeconds
    {
        get
        {
            lock (_gate)
            {
                return _session is { } session
                    ? Math.Clamp(
                        session.MediaNow() - CaptureSession.LeadSeconds,
                        0,
                        session.Options.Seconds
                    )
                    : 0;
            }
        }
    }

    // What An Ended Session Still Saves, Zero Once Let Go
    public double KeptSeconds
    {
        get
        {
            lock (_gate)
            {
                return _kept is { IsFresh: true } kept
                    ? Math.Clamp(
                        kept.Session.NewestEnd - CaptureSession.LeadSeconds,
                        0,
                        kept.Session.Options.Seconds
                    )
                    : 0;
            }
        }
    }

    // The Encoder Recording Now, Or Last, For About
    public VideoEncoder? Encoder { get; private set; }

    public CaptureOptions? Options
    {
        get
        {
            lock (_gate)
            {
                return _session?.Options;
            }
        }
    }

    #region Start And Stop
    public async Task StartAsync(CaptureOptions options)
    {
        await _transition.WaitAsync();
        try
        {
            if (!Status.CanStart)
            {
                return;
            }

            SetStatus(CaptureStatus.Idle with { State = CaptureState.Starting });
            var session = await Task.Run(() => LaunchAsync(options));
            lock (_gate)
            {
                _session = session;
                Encoder = session.Encoder;
            }

            _ = WatchEndAsync(session);
            _ = UpgradeAsync(session);
            UpdateStatus(session, CaptureState.Recording);
            _health.Change(s_healthPeriod, s_healthPeriod);
            s_logger.Info(
                $"Started Recording {(session.Options.GameWindow != 0 ? "A Game Window On " : "")}{session.Options.Monitor.Label} With {session.Encoder.Name}{(session.Encoder.GpuFrames ? " On The GPU" : "")} At {session.Size.Width} × {session.Size.Height}, {session.Options.FrameRate} FPS, {session.Options.Quality}, Keeping {session.Options.Seconds} s"
            );
        }
        catch (Exception exception)
        {
            // Whatever A Start Throws Ends On Error And Retries
            var message = exception.Message.TrimEnd('.');
            Failed?.Invoke(
                exception switch
                {
                    CaptureBlockedException => CaptureFailure.Blocked,
                    WindowRefusedException => CaptureFailure.WindowRefused,
                    _ => CaptureFailure.Brief,
                }
            );
            SetStatus(
                Status with
                {
                    State = CaptureState.Error,
                    Message = message,
                    Fix = exception is CaptureBlockedException blocked
                        ? blocked.Fix
                        : CaptureFix.None,
                }
            );
            s_logger.Warn($"Failed To Start Recording {exception.GetType().Name}, {message}");
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task StopAsync(string? reason, bool keepReplay = false)
    {
        await _transition.WaitAsync();
        try
        {
            CaptureSession? session;
            lock (_gate)
            {
                session = _session;
            }

            // A Session Gone To An Error Still Takes The Reason
            if (session is null)
            {
                if (Status.State == CaptureState.Error)
                {
                    SetStatus(
                        Status with
                        {
                            State = CaptureState.Stopped,
                            Message = reason,
                            Fix = CaptureFix.None,
                        }
                    );
                }

                return;
            }

            UpdateStatus(session, CaptureState.Stopping);
            await Task.Run(() => session.Shutdown(keepReplay));
            lock (_gate)
            {
                _session = null;
            }

            FinishRecordingOf(session);
            if (keepReplay)
            {
                Keep(session);
            }

            _health.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            SetStatus(
                Status with
                {
                    State = CaptureState.Stopped,
                    Message = reason,
                    Warning = null,
                    Fix = CaptureFix.None,
                }
            );
            s_logger.Info(reason ?? "Stopped Recording");
        }
        finally
        {
            _transition.Release();
        }
    }

    public void Adjust(CaptureOptions wanted)
    {
        // A New Length Or Volume Keeps The Session's History
        lock (_gate)
        {
            _session?.Adjust(wanted);
        }
    }

    public void MuteSystemAudio(TimeSpan duration)
    {
        lock (_gate)
        {
            _session?.Mixer?.MuteSystemAudio(duration);
        }
    }

    public async Task WaitForSavesAsync(TimeSpan timeout)
    {
        // Exit Lets A Running Save Finish, Within Reason
        var waited = Stopwatch.StartNew();
        while (Volatile.Read(ref _saves) > 0 && waited.Elapsed < timeout)
        {
            await Task.Delay(100);
        }
    }

    private static async Task<CaptureSession> LaunchAsync(CaptureOptions options)
    {
        if (!FFmpeg.IsAvailable)
        {
            throw new CaptureBlockedException(
                "Part of LetsClip is missing, reinstall it",
                CaptureFix.Reinstall
            );
        }

        // The Saved Monitor Is Found By Device Path, Never Number
        var monitor =
            MonitorCatalog.Find(MonitorCatalog.Enumerate(), options.Monitor.DevicePath)
            ?? throw new CaptureBlockedException(
                $"{options.Monitor.FriendlyName} is not connected",
                CaptureFix.Monitor
            );
        if (monitor.IsHdr)
        {
            throw new CaptureBlockedException(
                $"HDR is on for {monitor.FriendlyName}, turn it off in Display Settings to record in SDR",
                CaptureFix.Display
            );
        }

        // A Window Keeps Its Own Shape, No Bars
        var box = OutputSize(monitor, options.Resolution);
        var (width, height) =
            options.GameWindow != 0 && MonitorCatalog.ClientSizeOf(options.GameWindow) is { } client
                ? FitInside(box, client)
                : box;

        // Only What Outgrows Memory Waits On Disk, With Room Beside
        Directory.CreateDirectory(AppInfo.CacheDirectory);
        var spills =
            (
                VideoEncoders.AverageBytesPerSecond(
                    width,
                    height,
                    options.FrameRate,
                    options.Quality
                ) * options.Seconds
            ) - SegmentMemory.Budget;
        var needed = spills + StopFreeBytes;
        if (spills > 0 && FreeBytes(AppInfo.CacheDirectory) < needed)
        {
            throw new CaptureBlockedException(
                $"Less than {Formatter.FormatSize(needed)} free for a {Formatter.FormatDuration(options.Seconds)} replay, free some space or pick a shorter one",
                CaptureFix.Storage
            );
        }

        var skipped = new List<string>();

        // AMF Can Hang After A Killed Session, Two Starts
        var encoders = VideoEncoders
            .CaptureCandidates(ConvertsOnGpu(options with { Monitor = monitor }, width, height))
            .SelectMany(encoder =>
                Enumerable.Repeat(encoder, encoder.IsHardware && !encoder.GpuFrames ? 2 : 1)
            );
        foreach (var encoder in encoders)
        {
            // A Failed Or Hung Test Is Never Run Twice
            var unavailable = $"{encoder.Name} unavailable";
            if (
                skipped.Contains(unavailable)
                || !await VideoEncoders.WorksAsync(
                    encoder,
                    width,
                    height,
                    options.FrameRate,
                    CancellationToken.None
                )
            )
            {
                skipped.Add(unavailable);
                continue;
            }

            var session = new CaptureSession(
                options with
                {
                    Monitor = monitor,
                },
                encoder,
                width,
                height
            );
            try
            {
                await session.LaunchAsync();
                return session;
            }
            catch (Exception exception) when (IsStartFailure(exception))
            {
                skipped.Add($"{encoder.Name} failed");
                s_logger.Warn(
                    $"Failed To Capture With {encoder.Name}{(encoder.GpuFrames ? " On The GPU" : "")}, {exception.Message}"
                );
                session.Shutdown();

                // No Encoder Records A Window Windows Refused
                if (session.Video?.WindowRefused == true)
                {
                    throw new WindowRefusedException();
                }

                if (!encoder.IsHardware)
                {
                    throw;
                }
            }
        }

        throw new InvalidOperationException(string.Join(", ", skipped.Distinct()));
    }

    private async Task WatchEndAsync(CaptureSession session)
    {
        await session.Ended;
        if (session.Stopping)
        {
            return;
        }

        // A Closed Game Ends Its Capture First, No Error
        if (
            session.Options.GameWindow != 0
            && MonitorCatalog.ClientSizeOf(session.Options.GameWindow) is null
        )
        {
            await FailAsync(session, "Recording stopped, the window closed", windowClosed: true);
            return;
        }

        var exited = session.Video is { HasExited: true } video ? video : session.Audio;
        var reason =
            session.Video?.LastError
            ?? session.Audio?.LastError
            ?? $"the encoder exited with code {exited?.ExitCode}";
        await FailAsync(session, $"Recording stopped, {reason}");
    }

    private async Task FailAsync(
        CaptureSession session,
        string message,
        bool windowClosed = false,
        CaptureFix fix = CaptureFix.None
    )
    {
        // A Broken Session Ends, Its Finished Segments Kept A While
        await _transition.WaitAsync();
        try
        {
            lock (_gate)
            {
                if (_session != session)
                {
                    return;
                }

                _session = null;
            }

            await Task.Run(() => session.Shutdown(keepReplay: true));
            _health.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            FinishRecordingOf(session);
            Keep(session);

            // The Controller Hears First, A Quick Retry Shows No Error
            Failed?.Invoke(
                fix != CaptureFix.None ? CaptureFailure.Blocked
                : windowClosed ? CaptureFailure.WindowClosed
                : Stopwatch.GetElapsedTime(session.OriginTimestamp) > s_ranLong
                    ? CaptureFailure.RanLong
                : CaptureFailure.Brief
            );
            SetStatus(Status with { State = CaptureState.Error, Message = message, Fix = fix });
            if (windowClosed)
            {
                s_logger.Info("Stopped Recording, The Window Closed");
            }
            else
            {
                s_logger.Warn($"Stopped Recording With An Error, {message}");
            }
        }
        finally
        {
            _transition.Release();
        }
    }
    #endregion

    #region Status
    private void CheckHealth()
    {
        CaptureSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (
            session?.Video is not { } video
            || session.Stopping
            || session.OriginTimestamp == _stalledOrigin
            || Status.State != CaptureState.Recording
        )
        {
            return;
        }

        // Ponytail: Minimised Games Send Nothing, Only Their Exit Ends It
        var isWindow = session.Options.GameWindow != 0;
        var videoSince = isWindow ? TimeSpan.Zero : video.SinceLastSegment;
        var audioSince = session.Audio?.SinceLastSegment ?? TimeSpan.Zero;
        if ((videoSince > audioSince ? videoSince : audioSince) > s_stallTimeout)
        {
            s_logger.Warn(
                $"Stalled, {videoSince.TotalSeconds:0} s Since A Video Segment And {audioSince.TotalSeconds:0} s Since An Audio One"
            );
            _stalledOrigin = session.OriginTimestamp;
            _ = FailAsync(session, "Capture stalled, nothing arrived for several seconds");
            return;
        }

        // A Still Window And A Hung Encoder Look Alike
        var windowQuiet = isWindow && video.SinceLastSegment > s_stallTimeout;
        if (windowQuiet && !_windowQuiet)
        {
            _ = ProbeWindowAsync(session);
        }

        _windowQuiet = windowQuiet;

        if (_healthTicks % 10 == 0 && RecordingNeedsStop(session))
        {
            _ = EndRecordingAsync();
        }

        // A Nearly Full Disk Matters Only While Segments Spill
        if (
            ++_healthTicks % 10 == 0
            && SegmentMemory.SpilledWithin(TimeSpan.FromMinutes(1))
            && FreeBytes(session.Folder) < StopFreeBytes
        )
        {
            _ = FailAsync(
                session,
                "The disk is almost full, free some space and start again",
                fix: CaptureFix.Storage
            );
            return;
        }

        // A Problem That Clears Itself Is Never Shown
        _problemTicks = session.Mixer?.Problem is null ? 0 : _problemTicks + 1;

        if (!isWindow)
        {
            video.JudgeFrameRate(session.Options.FrameRate);
        }

        UpdateStatus(session, null);
    }

    private void UpdateStatus(CaptureSession session, CaptureState? state)
    {
        lock (_gate)
        {
            if (_session != session && state is null)
            {
                return;
            }

            var video = session.Video;
            var target = session.Options.FrameRate;
            var slow = video is { SlowReports: >= SlowTicks, FramesPerSecond: { } measured }
                ? target > 30
                    ? $"Capturing at {measured:0} FPS, below {target}, pick a lower frame rate"
                    : $"Capturing at {measured:0} FPS, below {target}"
                : null;
            var next = new CaptureStatus(
                state ?? Status.State,
                null,
                _saves > 0,
                (_problemTicks >= ProblemTicks ? session.Mixer?.Problem : null) ?? slow
            );

            // Only A Real Change Wakes The Tray And The Page
            if (next == Status)
            {
                return;
            }

            Status = next;
        }

        StatusChanged?.Invoke();
    }

    private void SetStatus(CaptureStatus status)
    {
        lock (_gate)
        {
            if (status == Status)
            {
                return;
            }

            Status = status;
        }

        StatusChanged?.Invoke();
    }
    #endregion

    #region Sizing
    internal static (int Width, int Height) OutputSize(MonitorInfo monitor, int resolution)
    {
        // A 16 By 9 Box, Or The Monitor's Own Size
        return resolution >= Math.Min(monitor.Width, monitor.Height)
            ? (Even(monitor.Width), Even(monitor.Height))
            : ClipExporter.OutputSize(monitor.Width, monitor.Height, resolution);
    }

    private static (int Width, int Height) FitInside(
        (int Width, int Height) box,
        (int Width, int Height) source
    )
    {
        // The Box Turns With The Window, Whatever Its Monitor
        if (box.Width > box.Height != source.Width > source.Height)
        {
            box = (box.Height, box.Width);
        }

        // Never Upscaled, Even Sizes For The Encoder
        var scale = Math.Min(
            1,
            Math.Min(box.Width / (double)source.Width, box.Height / (double)source.Height)
        );
        return (Even(source.Width * scale), Even(source.Height * scale));
    }

    private static int Even(double value)
    {
        return Math.Max(2, (int)Math.Round(value / 2) * 2);
    }

    private static long FreeBytes(string folder)
    {
        // An Unreadable Drive Is Treated As Full
        return ClipSaver.FreeBytes(folder) ?? 0;
    }
    #endregion

    public void Dispose()
    {
        _health.Dispose();
        CaptureSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Shutdown();
        DiscardKeptReplay();
        GC.SuppressFinalize(this);
    }
}
