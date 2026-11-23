using LetsClip.Data.Helpers;
using LetsClip.Services.Media;

namespace LetsClip.Services.Capture;

public sealed class ClipExtension(CaptureSession session, double start, double end, double limit)
{
    internal CaptureSession Session { get; } = session;

    // The Clip's Zero And Its End On The Session's Clock
    internal double Start { get; } = start;
    internal double End { get; set; } = end;

    // One Replay Length After The Press, Then It Lets Go
    internal double Limit { get; } = limit;
    internal double Pin { get; } = start - CaptureSession.SegmentSeconds;
    internal (long Size, DateTime Written) File { get; set; }
    internal int Holds { get; set; }
    internal bool Expired { get; set; }
    internal bool Forgotten { get; set; }
}

public sealed partial class ReplayRecorder
{
    // Less Than This After A Clip Is Not Worth Offering
    private const double ShortestAfter = 0.5;

    private readonly List<ClipExtension> _extensions = [];

    private void RememberSave(CaptureSession session, double start, double end, string path)
    {
        // Held One Replay Length, So What Came After Can Join
        var extension = new ClipExtension(session, start, end, end + session.Options.Seconds)
        {
            File = IdentityOf(path),
        };
        session.Pin(extension.Pin);
        lock (_gate)
        {
            _extensions.Add(extension);
        }

        _ = ExpireLaterAsync(extension);
    }

    private async Task ExpireLaterAsync(ClipExtension extension)
    {
        await Task.Delay(TimeSpan.FromSeconds(extension.Session.Options.Seconds + ShortestAfter));
        Expire(extension);
    }

    private void Expire(ClipExtension extension)
    {
        // An Editor Still Showing It Lets Go Once Left
        lock (_gate)
        {
            extension.Expired = true;
            if (extension.Holds > 0)
            {
                return;
            }
        }

        LetGo(extension);
    }

    private void LetGo(ClipExtension extension)
    {
        lock (_gate)
        {
            if (extension.Forgotten)
            {
                return;
            }

            extension.Forgotten = true;
            _extensions.Remove(extension);
        }

        extension.Session.Unpin(extension.Pin);
    }

    public void ForgetExtensions()
    {
        // A Lock Or Sleep Clears History, Shown Ones When Left
        List<ClipExtension> extensions;
        lock (_gate)
        {
            extensions = [.. _extensions];
        }

        foreach (var extension in extensions)
        {
            Expire(extension);
        }
    }

    public ClipExtension? ExtensionOf(string path)
    {
        // Only A Clip Just Saved, Still The Same File
        var file = IdentityOf(path);
        lock (_gate)
        {
            return _extensions.FirstOrDefault(extension =>
                !extension.Expired && extension.File == file
            );
        }
    }

    public static bool Matches(ClipExtension extension, string path)
    {
        // A Cut Or Restore Makes Another File, Renames Do Not
        return !extension.Forgotten && IdentityOf(path) == extension.File;
    }

    public static double AfterSeconds(ClipExtension extension)
    {
        // Recorded Since, Up To One Replay Length After The Press
        var after = Math.Min(extension.Session.NewestEnd, extension.Limit) - extension.End;
        return after >= ShortestAfter ? after : 0;
    }

    public bool TryHold(ClipExtension extension)
    {
        // Held While An Editor Shows It, Never Once Let Go
        lock (_gate)
        {
            if (extension.Forgotten)
            {
                return false;
            }

            extension.Holds++;
            return true;
        }
    }

    public void Release(ClipExtension extension)
    {
        bool ends;
        lock (_gate)
        {
            extension.Holds--;
            ends = extension.Holds == 0 && extension.Expired;
        }

        if (ends)
        {
            LetGo(extension);
        }
    }

    public static async Task<string?> AfterFileAsync(ClipExtension extension, double seconds)
    {
        // What Came After, Saved Apart For The Timeline's Pictures
        var after = Math.Min(seconds, AfterSeconds(extension));
        if (after <= 0)
        {
            return null;
        }

        var session = extension.Session;
        var end = extension.End + after;
        var folder = CacheFolders.NewFolder();
        var target = Path.Combine(folder, CacheFolders.AfterFile);
        await ClipSaver.RemuxToAsync(
            session.Video?.Segments.Covering(extension.End, end) ?? [],
            session.Audio?.Segments.Covering(extension.End, end) ?? [],
            extension.End,
            end,
            [],
            target
        );
        return target;
    }

    public static async Task ExtendAsync(
        ClipExtension extension,
        string path,
        double length,
        double trimStart
    )
    {
        // Saved Again From The Same Segments, Longer, Then Swapped In
        var session = extension.Session;
        var end = Math.Min(extension.Start + length, Math.Min(session.NewestEnd, extension.Limit));
        if (extension.Forgotten || end <= extension.End || IdentityOf(path) != extension.File)
        {
            throw new InvalidDataException("The clip changed, it can no longer grow");
        }

        var audio = session.Audio?.Segments.Covering(extension.Start, end) ?? [];
        await ClipSaver.ReplaceAsync(
            session.Video?.Segments.Covering(extension.Start, end) ?? [],
            audio,
            extension.Start,
            end,
            audio.Count > 0 ? session.Options.Stems : [],
            path
        );
        var duration = end - extension.Start;
        var kept = trimStart > 0 ? trimStart : (double?)null;
        await ClipLibrary.ReplacedAsync(
            path,
            stored =>
                stored with
                {
                    TrimStart = kept,
                    TrimEnd = kept is null ? null : duration,
                    Words = null,
                },
            duration
        );
        extension.End = end;
        extension.File = IdentityOf(path);
        s_logger.Info(
            $"Extended {Path.GetFileName(path)} To {Formatter.FormatDuration(duration)} With What Came After"
        );
    }

    private static (long Size, DateTime Written) IdentityOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.Length, file.LastWriteTimeUtc) : default;
    }
}
