using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    nint GameWindow,
    string? GameName,
    int GameProcess
);

public sealed record SaveOutcome(ClipInfo? Clip, string? Error);

public sealed class CaptureBlockedException(string message) : InvalidOperationException(message);

public sealed class ReplayRecorder : IDisposable
{
    // Free Space Needed To Start, And The Floor That Stops
    private const long MinimumFreeBytes = 2_000_000_000;
    private const long StopFreeBytes = 1_000_000_000;

    // Saves Wait Behind The Running One, A Few At Most
    private const int MaximumSaves = 3;

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
    private readonly SemaphoreSlim _saveTurn = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Timer _health;
    private CaptureSession? _session;
    private int _saves;
    private int _healthTicks;
    private int _problemTicks;
    private bool _windowQuiet;

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

    public CaptureStatus Status { get; private set; }

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
            }

            _ = WatchEndAsync(session);
            _ = UpgradeAsync(session);
            UpdateStatus(session, CaptureState.Recording);
            _health.Change(s_healthPeriod, s_healthPeriod);
            s_logger.Info(
                $"Started Recording {(session.Options.GameWindow != 0 ? "A Game Window On " : "")}{session.Options.Monitor.Label} With {session.Encoder.Name}{(session.Encoder.GpuFrames ? " On The GPU" : "")} At {session.Size.Width} × {session.Size.Height}, {session.Options.FrameRate} FPS, {session.Options.Quality}, Keeping {session.Options.Seconds} s"
            );
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            // Any Failed Start Shows As An Error
            var message = exception.Message.TrimEnd('.');
            Failed?.Invoke(
                exception is CaptureBlockedException ? CaptureFailure.Blocked : CaptureFailure.Brief
            );
            SetStatus(Status with { State = CaptureState.Error, Message = message });
            s_logger.Warn($"Failed To Start Recording {exception.GetType().Name}, {message}");
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task StopAsync(string? reason)
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
                    SetStatus(Status with { State = CaptureState.Stopped, Message = reason });
                }

                return;
            }

            UpdateStatus(session, CaptureState.Stopping);
            await Task.Run(session.Shutdown);
            lock (_gate)
            {
                _session = null;
            }

            _health.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            SetStatus(
                Status with
                {
                    State = CaptureState.Stopped,
                    Message = reason,
                    Warning = null,
                }
            );
            s_logger.Info(reason ?? "Stopped Recording");
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
            throw new CaptureBlockedException("Part of LetsClip is missing, reinstall it");
        }

        // The Saved Monitor Is Found By Device Path, Never Number
        var monitor =
            MonitorCatalog.Find(MonitorCatalog.Enumerate(), options.Monitor.DevicePath)
            ?? throw new CaptureBlockedException(
                $"{options.Monitor.FriendlyName} is not connected"
            );
        if (monitor.IsHdr)
        {
            throw new CaptureBlockedException(
                $"HDR is on for {monitor.FriendlyName}, turn it off in Display Settings to record in SDR"
            );
        }

        Directory.CreateDirectory(AppInfo.CacheDirectory);
        if (FreeBytes(AppInfo.CacheDirectory) < MinimumFreeBytes)
        {
            throw new CaptureBlockedException("Less than 2 GB free for the replay cache");
        }

        // A Window Keeps Its Own Shape, No Bars
        var box = OutputSize(monitor, options.Resolution);
        var (width, height) =
            options.GameWindow != 0 && MonitorCatalog.ClientSizeOf(options.GameWindow) is { } client
                ? FitInside(box, client)
                : box;
        var skipped = new List<string>();

        // AMF Can Hang After A Killed Session, Two Starts
        var encoders = VideoEncoders
            .Candidates()
            .SelectMany(encoder =>
                Enumerable.Repeat(encoder, encoder.IsHardware && !encoder.GpuFrames ? 2 : 1)
            );
        foreach (var encoder in encoders)
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
                skipped.Add($"{encoder.Name} unavailable");
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
        bool blocked = false
    )
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
            _health.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // The Controller Hears First, A Quick Retry Shows No Error
            Failed?.Invoke(
                blocked ? CaptureFailure.Blocked
                : windowClosed || Stopwatch.GetElapsedTime(session.OriginTimestamp) > s_ranLong
                    ? CaptureFailure.RanLong
                : CaptureFailure.Brief
            );
            SetStatus(Status with { State = CaptureState.Error, Message = message });
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

    #region Encoder Recovery
    private async Task UpgradeAsync(CaptureSession current)
    {
        // A Fallback Keeps Trying Better Encoders, Out Of Sight
        var better = VideoEncoders
            .Candidates()
            .TakeWhile(encoder => encoder != current.Encoder)
            .ToList();
        for (var attempt = 0; better.Count > 0; attempt++)
        {
            var wait = s_upgradeWaits[Math.Min(attempt, s_upgradeWaits.Length - 1)];
            await Task.WhenAny(Task.Delay(wait), current.Ended);
            if (current.Stopping || current.HasEnded)
            {
                return;
            }

            foreach (var encoder in better)
            {
                if (await TryMoveToAsync(current, encoder))
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> TryMoveToAsync(CaptureSession current, VideoEncoder encoder)
    {
        // Runs Beside The Fallback Until It Holds A Whole Replay
        var (width, height) = current.Size;
        var options = current.Options;
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
            return false;
        }

        var standby = new CaptureSession(options, encoder, width, height);
        try
        {
            await standby.LaunchAsync();
            await Task.WhenAny(
                Task.Delay(TimeSpan.FromSeconds(options.Seconds + CaptureSession.SegmentSeconds)),
                standby.Ended,
                current.Ended
            );
            if (!standby.HasEnded && await SwapAsync(current, standby))
            {
                return true;
            }
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            s_logger.Info($"Stayed On {current.Encoder.Name}, {encoder.Name}: {exception.Message}");
        }

        standby.Shutdown();
        return false;
    }

    private async Task<bool> SwapAsync(CaptureSession current, CaptureSession standby)
    {
        // Whole Replay Ready, So The Change Costs No History
        await _transition.WaitAsync();
        try
        {
            await WaitForSavesAsync(TimeSpan.FromSeconds(5));
            lock (_gate)
            {
                if (_session != current)
                {
                    return false;
                }

                _session = standby;
            }

            _ = WatchEndAsync(standby);
            _ = UpgradeAsync(standby);
            UpdateStatus(standby, null);
            await Task.Run(current.Shutdown);
            s_logger.Info($"Moved Recording From {current.Encoder.Name} To {standby.Encoder.Name}");
            return true;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task ProbeWindowAsync(CaptureSession current)
    {
        // Only A Hung Encoder Loses To A Fresh Capture
        var (width, height) = current.Size;
        var encoders = VideoEncoders
            .Candidates()
            .Where(encoder => encoder == current.Encoder || !encoder.IsHardware);
        foreach (var encoder in encoders)
        {
            if (current.Stopping || current.HasEnded)
            {
                return;
            }

            var standby = new CaptureSession(current.Options, encoder, width, height);
            try
            {
                await standby.LaunchAsync();
                await Task.WhenAny(
                    standby.Video!.FirstSegment,
                    Task.Delay(s_windowProbe),
                    current.Ended
                );

                // A Window That Just Moved Reaches The Old One Too
                await Task.Delay(s_windowWait);
                if (
                    standby.Video.FirstSegment.IsCompletedSuccessfully
                    && current.Video!.SinceLastSegment > s_stallTimeout
                    && await SwapAsync(current, standby)
                )
                {
                    return;
                }
            }
            catch (Exception exception) when (IsStartFailure(exception))
            {
                s_logger.Info(
                    $"Failed To Probe The Window With {encoder.Name}: {exception.Message}"
                );
            }

            standby.Shutdown();
        }

        s_logger.Debug("Kept The Capture Of A Still Window");
    }
    #endregion

    #region Saving
    public async Task<SaveOutcome> SaveAsync(int? monitor)
    {
        CaptureSession? session;
        double end;
        double pinned;
        var recordedAt = DateTimeOffset.Now;
        var pressed = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            session = _session;
            if (session is null || Status.State != CaptureState.Recording)
            {
                return new SaveOutcome(null, "Nothing is being recorded right now");
            }

            if (_saves >= MaximumSaves)
            {
                return new SaveOutcome(null, "Clips are already saving, try again in a moment");
            }

            // The Press Ends The Clip, However Long Saving Takes
            _saves++;
            end = session.MediaNow();
            pinned = end - session.Options.Seconds - (CaptureSession.SegmentSeconds * 2);
            session.Pin(pinned);
        }

        try
        {
            // Inside The Try, A Failing Handler Never Leaks The Pin
            UpdateStatus(session, null);
            SaveStarted?.Invoke();
            await _saveTurn.WaitAsync();
            try
            {
                var clip = await SaveFromAsync(session, end, recordedAt, monitor);
                ClipSaved?.Invoke(clip);
                s_logger.Info(
                    $"Saved {Path.GetFileName(clip.Path)} Of {Formatter.FormatDuration(clip.Duration)}, {Formatter.FormatSize(clip.SizeBytes)}, In {Stopwatch.GetElapsedTime(pressed).TotalSeconds:0.0} s"
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
                        or Win32Exception
            )
        {
            s_logger.Warn($"Failed To Save A Clip {exception.GetType().Name}, {exception.Message}");
            return new SaveOutcome(null, SaveErrorOf(exception));
        }
        finally
        {
            session.Unpin(pinned);
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
        DateTimeOffset recordedAt,
        int? monitor
    )
    {
        // Waits For The Press's Segment, A Still Window Sends None
        var isWindow = session.Options.GameWindow != 0;
        var waited = Stopwatch.StartNew();
        while (
            !session.HasEnded
            && waited.Elapsed < s_saveTimeout
            && (
                session.AudioEnd < end
                || (session.VideoEnd < end && !(isWindow && waited.Elapsed > s_windowWait))
            )
        )
        {
            await Task.Delay(100);
        }

        // Most Of A Save Is This Wait, Logged Apart
        s_logger.Debug($"Waited {waited.ElapsedMilliseconds} ms For The Press's Segments");
        var from = end - session.Options.Seconds;
        var video = session.Video?.Segments.Covering(from, end) ?? [];
        var to = Math.Min(end, session.NewestEnd);
        var videoOffset = 0.0;
        if (
            isWindow
            && session.VideoEnd < end
            && session.OpenVideoSegment() is { } open
            && video.All(segment => segment.Path != open.Path)
        )
        {
            // Its Last Frame Holds To The Press As Sound Runs
            videoOffset = Math.Max(0, from - open.Start);
            video = [.. video, open];
            to = Math.Min(end, session.AudioEnd);
        }

        if (video.Count == 0)
        {
            throw new InvalidDataException(
                isWindow ? "Nothing has moved in the window yet" : "Nothing recorded yet"
            );
        }

        // Exactly The Replay Length, Or All Of A Younger Session
        Task<ClipInfo> SaveClipAsync()
        {
            return ClipSaver.SaveAsync(
                video,
                session.Audio?.Segments.Covering(from, end) ?? [],
                Math.Max(from, CaptureSession.LeadSeconds),
                to,
                videoOffset,
                session.Folder,
                recordedAt,
                session.Options.FrameRate,
                session.Options.GameName,
                monitor,
                _library
            );
        }

        try
        {
            return await SaveClipAsync();
        }
        catch (IOException exception)
        {
            // A Scanner Holding The File Lets Go In A Moment
            s_logger.Info($"Saving Again After {exception.GetType().Name}");
            await Task.Delay(TimeSpan.FromSeconds(1));
            return await SaveClipAsync();
        }
    }

    private static string SaveErrorOf(Exception exception)
    {
        // Plain Words, Never A Path Or An Error Code
        return exception switch
        {
            UnauthorizedAccessException => "Cannot write to the clips folder",
            DirectoryNotFoundException => "The clips folder is not available",
            IOException => "The clip could not be written",
            TimeoutException => "Saving took too long, try again",
            Win32Exception => "The recorder could not start, reinstall LetsClip",
            _ => exception.Message,
        };
    }

    private static bool IsStartFailure(Exception exception)
    {
        // Slow Tests, Blocked FFmpeg, Cache Or Audio Problems Fail Starts
        return exception
            is TimeoutException
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidOperationException
                or COMException
                or NotSupportedException;
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

        if (++_healthTicks % 10 == 0 && FreeBytes(session.Folder) < StopFreeBytes)
        {
            _ = FailAsync(
                session,
                "The disk is almost full, free some space and start again",
                blocked: true
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
        GC.SuppressFinalize(this);
    }
}
