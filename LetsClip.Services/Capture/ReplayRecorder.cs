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
    bool FullResolution,
    AudioDeviceInfo? SystemAudio,
    AudioDeviceInfo? Microphone
);

public sealed record SaveOutcome(ClipInfo? Clip, string? Error);

public sealed class CaptureException(string message) : Exception(message);

public sealed class ReplayRecorder : IDisposable
{
    // Free Space Needed To Start, And The Floor That Stops
    private const long MinimumFreeBytes = 2_000_000_000;
    private const long StopFreeBytes = 1_000_000_000;

    // Saves Wait Behind The Running One, A Few At Most
    private const int MaximumSaves = 3;

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan s_stallTimeout = TimeSpan.FromSeconds(8);

    private readonly ClipLibrary _library;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly SemaphoreSlim _saveTurn = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Timer _health;
    private CaptureSession? _session;
    private int _saves;
    private int _healthTicks;

    public ReplayRecorder(ClipLibrary library, int seconds)
    {
        _library = library;
        Status = CaptureStatus.Idle(seconds);
        _health = new Timer(
            _ => CheckHealth(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)
        );
    }

    public event Action? StatusChanged;

    public event Action<ClipInfo>? ClipSaved;

    public CaptureStatus Status { get; private set; }

    public void ShowIdle(int seconds)
    {
        // Settings Changed While Stopped Show Before The Next Start
        lock (_gate)
        {
            if (_session is not null)
            {
                return;
            }

            Status = Status with { RequestedSeconds = seconds };
        }

        StatusChanged?.Invoke();
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

            SetStatus(
                CaptureStatus.Idle(options.Seconds) with
                {
                    State = CaptureState.Starting,
                    MonitorLabel = options.Monitor.Label,
                }
            );
            var session = await Task.Run(() => LaunchAsync(options));
            lock (_gate)
            {
                _session = session;
            }

            _ = WatchEndAsync(session);
            UpdateStatus(session, CaptureState.Buffering);
            s_logger.Info(
                $"Started Buffering {session.Options.Monitor.Label} With {session.Encoder.Name}"
            );
        }
        catch (CaptureException exception)
        {
            SetStatus(Status with { State = CaptureState.Error, Message = exception.Message });
            s_logger.Warn($"Failed To Start Buffering, {exception.Message}");
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task StopAsync(string? reason = null)
    {
        await _transition.WaitAsync();
        try
        {
            CaptureSession? session;
            lock (_gate)
            {
                session = _session;
            }

            if (session is null)
            {
                return;
            }

            UpdateStatus(session, CaptureState.Stopping);
            await Task.Run(session.Shutdown);
            lock (_gate)
            {
                _session = null;
            }

            SetStatus(
                Status with
                {
                    State = CaptureState.Stopped,
                    Message = reason,
                    AvailableSeconds = 0,
                    Warning = null,
                }
            );
            s_logger.Info(reason is null ? "Stopped Buffering" : $"Stopped Buffering, {reason}");
        }
        finally
        {
            _transition.Release();
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
            throw new CaptureException("FFmpeg is missing beside LetsClip, reinstall it");
        }

        // The Saved Monitor Is Found By Device Path, Never Number
        var monitor =
            MonitorCatalog
                .Enumerate()
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.DevicePath,
                        options.Monitor.DevicePath,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            ?? throw new CaptureException($"{options.Monitor.FriendlyName} is not connected");
        if (monitor.IsHdr)
        {
            throw new CaptureException(
                $"HDR is on for {monitor.FriendlyName}, turn it off in Display Settings to record in SDR"
            );
        }

        Directory.CreateDirectory(AppInfo.CacheDirectory);
        if (FreeBytes(AppInfo.CacheDirectory) < MinimumFreeBytes)
        {
            throw new CaptureException("Less than 2 GB free for the replay cache");
        }

        var (width, height) = OutputSize(monitor, options.FullResolution);
        var skipped = new List<string>();
        foreach (var encoder in VideoEncoders.Candidates(MonitorCatalog.AdapterVendors()))
        {
            if (
                !await VideoEncoders.WorksAsync(
                    encoder,
                    width,
                    height,
                    options.FrameRate,
                    CancellationToken.None
                )
            )
            {
                skipped.Add($"{encoder.Name} Unavailable");
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
                await session.LaunchAsync(s_startTimeout);
                if (skipped.Count > 0)
                {
                    session.Warning = $"Using {encoder.Name}, {string.Join(", ", skipped)}";
                }

                return session;
            }
            catch (CaptureException exception)
            {
                skipped.Add($"{encoder.Name} Failed");
                s_logger.Warn($"Failed To Capture With {encoder.Name}, {exception.Message}");
                session.Shutdown();
                if (!encoder.IsHardware)
                {
                    throw;
                }
            }
        }

        throw new CaptureException(string.Join(", ", skipped));
    }

    private async Task WatchEndAsync(CaptureSession session)
    {
        await session.Ended;
        if (!session.Stopping)
        {
            var reason =
                session.Video?.LastError ?? session.Audio?.LastError ?? "The encoder exited";
            await FailAsync(session, $"Recording stopped, {reason}");
        }
    }

    private async Task FailAsync(CaptureSession session, string message)
    {
        // A Broken Session Ends Whole, Its History Untrusted
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

            await Task.Run(session.Shutdown);
            SetStatus(
                Status with
                {
                    State = CaptureState.Error,
                    Message = message,
                    AvailableSeconds = 0,
                }
            );
            s_logger.Warn($"Stopped Buffering With An Error, {message}");
        }
        finally
        {
            _transition.Release();
        }
    }
    #endregion

    #region Saving
    public async Task<SaveOutcome> SaveAsync()
    {
        CaptureSession? session;
        double end;
        IDisposable pin;
        var recordedAt = DateTimeOffset.Now;
        lock (_gate)
        {
            session = _session;
            if (session is null || Status.State != CaptureState.Buffering)
            {
                return new SaveOutcome(null, "Start buffering first");
            }

            if (_saves >= MaximumSaves)
            {
                return new SaveOutcome(null, "Clips are already saving, try again in a moment");
            }

            // The Press Ends The Clip, However Long Saving Takes
            _saves++;
            end = session.MediaNow();
            pin = session.Pin(end - session.Options.Seconds - (CaptureSession.SegmentSeconds * 2));
        }

        UpdateStatus(session, null);
        try
        {
            await _saveTurn.WaitAsync();
            try
            {
                var clip = await SaveFromAsync(session, end, recordedAt);
                ClipSaved?.Invoke(clip);
                s_logger.Info(
                    $"Saved {clip.FileName} Of {Formatter.FormatDuration(clip.Duration)}"
                );
                return new SaveOutcome(clip, null);
            }
            finally
            {
                _saveTurn.Release();
            }
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or TimeoutException
            )
        {
            s_logger.Warn($"Failed To Save A Clip {exception.GetType().Name}, {exception.Message}");
            return new SaveOutcome(
                null,
                exception is UnauthorizedAccessException
                    ? "Cannot write to the clips folder"
                    : exception.Message
            );
        }
        finally
        {
            pin.Dispose();
            lock (_gate)
            {
                _saves--;
            }

            UpdateStatus(session, null);
        }
    }

    private async Task<ClipInfo> SaveFromAsync(
        CaptureSession session,
        double end,
        DateTimeOffset recordedAt
    )
    {
        // The Segment Holding The Press Closes At The Next Keyframe
        var waited = Stopwatch.StartNew();
        while (session.NewestEnd < end && !session.HasEnded && waited.Elapsed < s_stallTimeout)
        {
            await Task.Delay(100);
        }

        var from = end - session.Options.Seconds;
        var video = session.VideoCovering(from, end);
        if (video.Count == 0)
        {
            throw new InvalidDataException("Nothing recorded yet");
        }

        return await ClipSaver.SaveAsync(
            video,
            session.AudioCovering(from, end),
            from,
            Math.Min(end, session.NewestEnd),
            session.Folder,
            _library.Folder,
            session.Options.Monitor.FriendlyName,
            recordedAt,
            _library,
            CancellationToken.None
        );
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
            || Status.State != CaptureState.Buffering
        )
        {
            return;
        }

        // A Stalled Encoder Or Nearly Full Disk Ends The Session
        var since =
            session.Audio is { } audio && audio.SinceLastSegment > video.SinceLastSegment
                ? audio.SinceLastSegment
                : video.SinceLastSegment;
        if (since > s_stallTimeout)
        {
            _ = FailAsync(session, "Capture stalled, nothing arrived for several seconds");
            return;
        }

        if (++_healthTicks % 10 == 0 && FreeBytes(session.Folder) < StopFreeBytes)
        {
            _ = FailAsync(session, "The disk is almost full, free some space and start again");
            return;
        }

        video.JudgeFrameRate(session.Options.FrameRate);
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

            var newest = session.NewestEnd;
            var available =
                newest > 0
                    ? Math.Clamp(
                        session.MediaNow() - session.OldestStart,
                        0,
                        session.Options.Seconds
                    )
                    : 0;
            var video = session.Video;
            var target = session.Options.FrameRate;
            var slow = video is { SlowReports: >= 3, FramesPerSecond: { } measured }
                ? target > 30
                    ? $"Capturing at {measured:0} FPS, below {target}, pick a lower frame rate"
                    : $"Capturing at {measured:0} FPS, below {target}"
                : null;
            Status = new CaptureStatus(
                state ?? Status.State,
                null,
                session.Options.Monitor.Label,
                session.Options.Seconds,
                available,
                _saves > 0,
                Math.Max(0, _saves - 1),
                session.Mixer?.Problem ?? slow ?? session.Warning
            );
        }

        StatusChanged?.Invoke();
    }

    private void SetStatus(CaptureStatus status)
    {
        lock (_gate)
        {
            Status = status;
        }

        StatusChanged?.Invoke();
    }

    #endregion

    #region Sizing
    public static (int Width, int Height) OutputSize(MonitorInfo monitor, bool fullResolution)
    {
        if (fullResolution)
        {
            return (Even(monitor.Width), Even(monitor.Height));
        }

        // Fits Inside 1080p In The Monitor's Own Orientation, Never Upscaled
        var portrait = monitor.Height > monitor.Width;
        var boxWidth = portrait ? 1080 : 1920;
        var boxHeight = portrait ? 1920 : 1080;
        var scale = Math.Min(
            1,
            Math.Min(boxWidth / (double)monitor.Width, boxHeight / (double)monitor.Height)
        );
        return (Even(monitor.Width * scale), Even(monitor.Height * scale));
    }

    private static int Even(double value)
    {
        return Math.Max(2, (int)Math.Round(value / 2) * 2);
    }

    private static long FreeBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception exception)
            when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // An Unreadable Drive Is Treated As Full
            return 0;
        }
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
        _transition.Dispose();
        _saveTurn.Dispose();
        GC.SuppressFinalize(this);
    }
}
