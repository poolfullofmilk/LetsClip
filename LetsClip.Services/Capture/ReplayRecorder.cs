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
    string? GameName
);

public sealed record SaveOutcome(ClipInfo? Clip, string? Error);

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

    // A Session This Old Had Worked, Its Retry Comes Quick
    private static readonly TimeSpan s_ranLong = TimeSpan.FromSeconds(30);

    private readonly ClipLibrary _library;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly SemaphoreSlim _saveTurn = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Timer _health;
    private CaptureSession? _session;
    private int _saves;
    private int _healthTicks;

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

    // Every Error, True When The Session Had Run A While
    public event Action<bool>? Failed;

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
            Failed?.Invoke(false);
            SetStatus(Status with { State = CaptureState.Error, Message = message });
            s_logger.Warn($"Failed To Start Recording {exception.GetType().Name}, {message}");
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
            throw new InvalidOperationException("FFmpeg is missing beside LetsClip, reinstall it");
        }

        // The Saved Monitor Is Found By Device Path, Never Number
        var monitor =
            MonitorCatalog.Find(MonitorCatalog.Enumerate(), options.Monitor.DevicePath)
            ?? throw new InvalidOperationException(
                $"{options.Monitor.FriendlyName} is not connected"
            );
        if (monitor.IsHdr)
        {
            throw new InvalidOperationException(
                $"HDR is on for {monitor.FriendlyName}, turn it off in Display Settings to record in SDR"
            );
        }

        Directory.CreateDirectory(AppInfo.CacheDirectory);
        if (FreeBytes(AppInfo.CacheDirectory) < MinimumFreeBytes)
        {
            throw new InvalidOperationException("Less than 2 GB free for the replay cache");
        }

        // A Window Keeps Its Own Shape, No Bars
        var box = OutputSize(monitor, options.Resolution);
        var (width, height) =
            options.GameWindow != 0 && MonitorCatalog.ClientSizeOf(options.GameWindow) is { } client
                ? FitInside(box, client)
                : box;
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
                if (!encoder.GpuFrames)
                {
                    skipped.Add($"{encoder.Name} unavailable");
                }

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
                if (skipped.Count > 0)
                {
                    session.Warning = $"Using {encoder.Name}, {string.Join(", ", skipped)}";
                }

                return session;
            }
            catch (Exception exception) when (IsStartFailure(exception))
            {
                // A GPU Route Falling Back Is No News
                if (!encoder.GpuFrames)
                {
                    skipped.Add($"{encoder.Name} failed");
                }

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

        throw new InvalidOperationException(string.Join(", ", skipped));
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
            await FailAsync(session, "Recording stopped, the window closed", quiet: true);
            return;
        }

        var exited = session.Video is { HasExited: true } video ? video : session.Audio;
        var reason =
            session.Video?.LastError
            ?? session.Audio?.LastError
            ?? $"the encoder exited with code {exited?.ExitCode}";
        await FailAsync(session, $"Recording stopped, {reason}");
    }

    private async Task FailAsync(CaptureSession session, string message, bool quiet = false)
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
            Failed?.Invoke(quiet || Stopwatch.GetElapsedTime(session.OriginTimestamp) > s_ranLong);
            SetStatus(Status with { State = CaptureState.Error, Message = message });
            if (quiet)
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

    #region Saving
    public async Task<SaveOutcome> SaveAsync()
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
                var clip = await SaveFromAsync(session, end, recordedAt);
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
        DateTimeOffset recordedAt
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
        return await ClipSaver.SaveAsync(
            video,
            session.Audio?.Segments.Covering(from, end) ?? [],
            Math.Max(from, CaptureSession.LeadSeconds),
            to,
            videoOffset,
            session.Folder,
            recordedAt,
            session.Options.FrameRate,
            session.Options.GameName,
            _library
        );
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
            Win32Exception => "FFmpeg could not start, reinstall LetsClip",
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

        if (++_healthTicks % 10 == 0 && FreeBytes(session.Folder) < StopFreeBytes)
        {
            _ = FailAsync(session, "The disk is almost full, free some space and start again");
            return;
        }

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
            var slow = video is { SlowReports: >= 3, FramesPerSecond: { } measured }
                ? target > 30
                    ? $"Capturing at {measured:0} FPS, below {target}, pick a lower frame rate"
                    : $"Capturing at {measured:0} FPS, below {target}"
                : null;
            var next = new CaptureStatus(
                state ?? Status.State,
                null,
                _saves > 0,
                session.Mixer?.Problem ?? slow ?? session.Warning
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
    private static (int Width, int Height) OutputSize(MonitorInfo monitor, int resolution)
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
        _transition.Dispose();
        _saveTurn.Dispose();
        GC.SuppressFinalize(this);
    }
}
