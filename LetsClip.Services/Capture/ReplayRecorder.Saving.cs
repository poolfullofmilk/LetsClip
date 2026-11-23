using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;

namespace LetsClip.Services.Capture;

public sealed partial class ReplayRecorder
{
    public async Task<SaveOutcome> SaveAsync(Func<CaptureOptions, int?> monitorOf)
    {
        CaptureSession? session;
        KeptReplay? kept;
        var end = 0.0;
        var pinned = 0.0;
        var recordedAt = DateTimeOffset.Now;
        var pressed = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            session = Status.State == CaptureState.Recording ? _session : null;

            // An Ended Session Counts While The Running One Holds Less
            kept =
                _kept is { IsFresh: true } fresh
                && (
                    session is null
                    || session.MediaNow() - CaptureSession.LeadSeconds < session.Options.Seconds
                )
                    ? fresh
                    : null;
            if (session is null && kept is null)
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
            if (session is not null)
            {
                end = session.MediaNow();
                pinned = end - session.Options.Seconds - (CaptureSession.SegmentSeconds * 2);
                session.Pin(pinned);
            }

            if (kept is not null)
            {
                kept.Saved = true;
                kept.Session.Pin(0);
            }
        }

        try
        {
            // Inside The Try, A Failing Handler Never Leaks The Pin
            RefreshSaving(session);
            SaveStarted?.Invoke();
            List<SaveOutcome> outcomes = [];
            if (kept is not null)
            {
                outcomes.Add(
                    await SaveSafelyAsync(
                        () =>
                            SaveFromAsync(
                                kept.Session,
                                kept.Session.NewestEnd,
                                kept.EndedAt,
                                monitorOf(kept.Session.Options)
                            ),
                        pressed,
                        " Kept From The Ended Session, Of",
                        "The Ended Session's Replay"
                    )
                );
            }

            if (session is not null)
            {
                outcomes.Add(
                    await SaveSafelyAsync(
                        () =>
                            SaveFromAsync(
                                session,
                                end,
                                recordedAt,
                                monitorOf(session.Options),
                                saved: (start, to, path) => RememberSave(session, start, to, path)
                            ),
                        pressed,
                        " Of",
                        "A Clip"
                    )
                );
            }

            // Any Part That Failed Is Said, Else The Newest Clip
            return outcomes.FirstOrDefault(outcome => outcome.Error is not null) ?? outcomes[^1];
        }
        finally
        {
            session?.Unpin(pinned);
            kept?.Session.Unpin(0);
            lock (_gate)
            {
                _saves--;
            }

            RefreshSaving(session);
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
            return await SaveSafelyAsync(
                () =>
                    SaveFromAsync(
                        manual.Session,
                        end,
                        manual.StartedAt,
                        manual.Monitor,
                        manual.From
                    ),
                stopped,
                " Recorded Until Stopped,",
                "A Recording"
            );
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

    private async Task<SaveOutcome> SaveSafelyAsync(
        Func<Task<ClipInfo>> save,
        long started,
        string saved,
        string failed
    )
    {
        // Every Save Logs Alike, Failures Read In Plain Words
        try
        {
            var clip = await save();
            ClipSaved?.Invoke(clip);
            s_logger.Info(
                $"Saved {Path.GetFileName(clip.Path)}{saved} {Formatter.FormatDuration(clip.Duration)}, {Formatter.FormatSize(clip.SizeBytes)}, In {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s"
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
                $"Failed To Save {failed} {exception.GetType().Name}, {exception.Message}"
            );
            return new SaveOutcome(null, SaveErrorOf(exception));
        }
    }

    private void RefreshSaving(CaptureSession? session)
    {
        // Saving Shows Even When Only An Ended Session Saves
        if (session is not null)
        {
            UpdateStatus(session, null);
            return;
        }

        SetStatus(Status with { IsSaving = Volatile.Read(ref _saves) > 0 });
    }

    private void Keep(CaptureSession session)
    {
        // An Empty Session Has Nothing Worth Keeping
        if (session.NewestEnd <= CaptureSession.LeadSeconds)
        {
            session.Discard();
            return;
        }

        // Saveable For Its Own Length After Ending, Then Let Go
        var kept = new KeptReplay(session);
        KeptReplay? replaced;
        lock (_gate)
        {
            replaced = _kept;
            _kept = kept;
        }

        replaced?.Session.Discard();
        _ = ForgetLaterAsync(kept);
        s_logger.Info(
            $"Kept The Ended Session's Replay For {Formatter.FormatDuration(session.Options.Seconds)}"
        );
        StatusChanged?.Invoke();
    }

    private async Task ForgetLaterAsync(KeptReplay kept)
    {
        await Task.Delay(TimeSpan.FromSeconds(kept.Session.Options.Seconds));
        Forget(kept);
    }

    public void DiscardKeptReplay()
    {
        Forget(null);
    }

    private void Forget(KeptReplay? only)
    {
        // An Older Timer Never Lets Go Of A Newer Replay
        KeptReplay? kept;
        lock (_gate)
        {
            kept = _kept;
            if (kept is null || (only is not null && kept != only))
            {
                return;
            }

            _kept = null;
        }

        kept.Session.Discard();
        StatusChanged?.Invoke();
    }

    private sealed class KeptReplay(CaptureSession session)
    {
        private readonly long _ended = Stopwatch.GetTimestamp();

        public CaptureSession Session { get; } = session;

        public DateTimeOffset EndedAt { get; } = DateTimeOffset.Now;

        // Saved Once, Then Only The Running Session Saves
        public bool Saved { get; set; }

        public bool IsFresh =>
            !Saved && Stopwatch.GetElapsedTime(_ended).TotalSeconds < Session.Options.Seconds;
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
        double? since = null,
        Action<double, double, string>? saved = null
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

        // A Still Window's Open Segment Is A Copy, Freed After
        var open = isWindow && session.VideoEnd < end ? session.OpenVideoSegment() : null;
        try
        {
            if (open is not null)
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
            var history = Math.Max(video[0].Start, audio.Count > 0 ? audio[0].Start : 0);
            var wanted = Math.Max(from, CaptureSession.LeadSeconds);
            var start =
                videoOffset == 0 && history - wanted > CaptureSession.SegmentSeconds
                    ? history
                    : wanted;

            // Exactly The Replay Length, Or All Of A Younger Session
            var clip = await ClipSaver.SaveAsync(
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
                audio.Count > 0 ? session.Options.Stems : [],
                _library
            );

            // A Moving Picture's Clip Can Take What Comes After
            if (videoOffset == 0)
            {
                saved?.Invoke(start, to, clip.Path);
            }

            return clip;
        }
        finally
        {
            open?.Release();
        }
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
