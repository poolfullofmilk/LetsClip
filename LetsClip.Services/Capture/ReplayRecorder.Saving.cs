using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Media;

namespace LetsClip.Services.Capture;

public sealed partial class ReplayRecorder
{
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
                s_logger.Info(
                    $"Saved Nothing For A Press, Nothing Is Recording, State {Status.State}"
                );
                return new SaveOutcome(null, "Nothing is being recorded right now");
            }

            // A Press While One Saves Does Nothing
            if (_saves > 0)
            {
                s_logger.Info("Ignored A Press, A Save Is Running");
                return new SaveOutcome(null, null);
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
            var clip = await SaveFromAsync(session, end, recordedAt, monitor);
            ClipSaved?.Invoke(clip);
            s_logger.Info(
                $"Saved {Path.GetFileName(clip.Path)} Of {Formatter.FormatDuration(clip.Duration)}, {Formatter.FormatSize(clip.SizeBytes)}, In {Stopwatch.GetElapsedTime(pressed).TotalSeconds:0.0} s"
            );
            return new SaveOutcome(clip, null);
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

    public bool StartRecording(int? monitor)
    {
        // Pinned From Now, Every Segment Stays Until Stop Saves It
        lock (_gate)
        {
            if (
                _session is not { } session
                || Status.State != CaptureState.Recording
                || _manual is not null
            )
            {
                return false;
            }

            var from = session.MediaNow();
            session.Pin(from);
            _manual = new ManualRecording(session, from, DateTimeOffset.Now, monitor);
        }

        s_logger.Info("Started Recording Until Stopped");
        StatusChanged?.Invoke();
        return true;
    }

    public async Task<SaveOutcome> StopRecordingAsync()
    {
        ManualRecording? manual;
        double end;
        lock (_gate)
        {
            manual = _manual;
            if (manual is null)
            {
                // Already Stopped Elsewhere, Nothing To Save Or Say
                return new SaveOutcome(null, null);
            }

            _manual = null;
            _saves++;
            var session = manual.Session;
            end = session.HasEnded ? session.NewestEnd : session.MediaNow();
        }

        var stopped = Stopwatch.GetTimestamp();
        try
        {
            StatusChanged?.Invoke();
            SaveStarted?.Invoke();

            var clip = await SaveFromAsync(
                manual.Session,
                end,
                manual.StartedAt,
                manual.Monitor,
                manual.From
            );
            ClipSaved?.Invoke(clip);
            s_logger.Info(
                $"Saved {Path.GetFileName(clip.Path)} Recorded Until Stopped, {Formatter.FormatDuration(clip.Duration)}, {Formatter.FormatSize(clip.SizeBytes)}, In {Stopwatch.GetElapsedTime(stopped).TotalSeconds:0.0} s"
            );
            return new SaveOutcome(clip, null);
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
            s_logger.Warn(
                $"Failed To Save A Recording {exception.GetType().Name}, {exception.Message}"
            );
            return new SaveOutcome(null, SaveErrorOf(exception));
        }
        finally
        {
            manual.Session.Unpin(manual.From);
            lock (_gate)
            {
                _saves--;
            }

            UpdateStatus(manual.Session, null);
            StatusChanged?.Invoke();
        }
    }

    private void FinishRecordingOf(CaptureSession session)
    {
        // A Session Ending Ends Its Recording, Saving What It Holds
        bool ends;
        lock (_gate)
        {
            ends = _manual?.Session == session;
        }

        if (ends)
        {
            _ = EndRecordingAsync();
        }
    }

    private async Task EndRecordingAsync()
    {
        // Nobody Pressed Stop, A Failed Save Says So Itself
        if ((await StopRecordingAsync()).Error is { } error)
        {
            RecordingNotSaved?.Invoke(error);
        }
    }

    private bool RecordingNeedsStop(CaptureSession session)
    {
        // Stopped Before The Clips Drive Cannot Hold The Recording
        ManualRecording? manual;
        lock (_gate)
        {
            manual = _manual;
        }

        if (manual?.Session != session || ClipSaver.FreeBytes(_library.Folder) is not { } free)
        {
            return false;
        }

        var now = session.MediaNow();
        var recorded =
            (session.Video?.Segments.Covering(manual.From, now).Sum(segment => segment.Bytes) ?? 0)
            + (
                session.Audio?.Segments.Covering(manual.From, now).Sum(segment => segment.Bytes)
                ?? 0
            );
        if (free >= recorded + StopFreeBytes)
        {
            return false;
        }

        s_logger.Warn("Stopped Recording Until Stopped, The Clips Drive Is Nearly Full");
        return true;
    }

    private sealed record ManualRecording(
        CaptureSession Session,
        double From,
        DateTimeOffset StartedAt,
        int? Monitor
    );

    private async Task<ClipInfo> SaveFromAsync(
        CaptureSession session,
        double end,
        DateTimeOffset recordedAt,
        int? monitor,
        double? since = null
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
            // Woken By The Next Segment, Else Checked Again Soon
            using var poll = new CancellationTokenSource();
            await Task.WhenAny(session.SegmentAdded, Task.Delay(100, poll.Token));
            poll.Cancel();
        }

        // Most Of A Save Is This Wait, Logged Apart
        s_logger.Debug($"Waited {waited.ElapsedMilliseconds} ms For The Press's Segments");
        var from = since ?? end - session.Options.Seconds;
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

        // A Replay Just Made Longer Starts Where Its History Does
        var audio = session.Audio?.Segments.Covering(from, end) ?? [];
        var kept = Math.Max(video[0].Start, audio.Count > 0 ? audio[0].Start : 0);
        var wanted = Math.Max(from, CaptureSession.LeadSeconds);
        var start =
            videoOffset == 0 && kept - wanted > CaptureSession.SegmentSeconds ? kept : wanted;

        // Exactly The Replay Length, Or All Of A Younger Session
        return await ClipSaver.SaveAsync(
            video,
            audio,
            start,
            to,
            videoOffset,
            recordedAt,
            session.Options.FrameRate,
            session.Size,
            session.Options.GameName,
            monitor,
            session.Options.SeparatesMicrophone && audio.Count > 0,
            _library
        );
    }

    // Windows Says A Write Hit A Full Disk Either Way
    private const int DiskHandleFullCode = unchecked((int)0x80070027);

    private static string SaveErrorOf(Exception exception)
    {
        // Plain Words, Never A Path Or An Error Code
        return exception switch
        {
            UnauthorizedAccessException => "Cannot write to the clips folder",
            DirectoryNotFoundException => "The clips folder is not available",
            IOException { HResult: ClipSaver.DiskFullCode or DiskHandleFullCode } =>
                "The clips drive is full, free some space",
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
}
