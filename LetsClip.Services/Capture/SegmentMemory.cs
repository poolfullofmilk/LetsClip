using System.Diagnostics;
using System.Globalization;
using LetsClip.Data.Helpers;
using NLog;

namespace LetsClip.Services.Capture;

public static class SegmentMemory
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // A Gigabyte Or A Sixteenth, A Sandbox May Set Less
    public static long Budget { get; } =
        Environment.GetEnvironmentVariable("LETSCLIP_MEMORY_MB") is { } megabytes
        && AppInfo.SandboxRoot is not null
            ? long.Parse(megabytes, CultureInfo.InvariantCulture) << 20
            : Math.Min(1L << 30, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16);

    // Every Cache Shares One Budget, A Kept Replay Included
    private static readonly Lock s_gate = new();
    private static readonly List<SegmentCache> s_caches = [];
    private static long s_held;
    private static long s_lastSpill;

    public static long Held => Interlocked.Read(ref s_held);

    internal static void Count(long bytes)
    {
        Interlocked.Add(ref s_held, bytes);
    }

    public static void Register(SegmentCache cache)
    {
        lock (s_gate)
        {
            s_caches.Add(cache);
        }
    }

    public static void Unregister(SegmentCache cache)
    {
        lock (s_gate)
        {
            s_caches.Remove(cache);
        }
    }

    public static bool SpilledWithin(TimeSpan span)
    {
        var last = Interlocked.Read(ref s_lastSpill);
        return last != 0 && Stopwatch.GetElapsedTime(last) < span;
    }

    public static void SpillOver()
    {
        // Past The Budget The Oldest Arrivals Wait On Disk
        if (Held <= Budget)
        {
            return;
        }

        lock (s_gate)
        {
            var oldest = s_caches
                .SelectMany(cache => cache.InMemory())
                .OrderBy(segment => segment.Arrived)
                .ToList();
            foreach (var segment in oldest)
            {
                if (Held <= Budget)
                {
                    break;
                }

                if (!SpilledWithin(TimeSpan.FromMinutes(1)))
                {
                    s_logger.Info(
                        $"Began Moving The Oldest Segments To Disk, Past {Formatter.FormatSize(Budget)} In Memory"
                    );
                }

                Interlocked.Exchange(ref s_lastSpill, Stopwatch.GetTimestamp());
                try
                {
                    segment.Spill();
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    // Kept In Memory, A Full Disk Stops The Session Soon
                    s_logger.Warn($"Failed To Move A Segment To Disk {exception.GetType().Name}");
                    return;
                }
            }
        }
    }
}
