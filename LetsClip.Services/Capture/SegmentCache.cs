using System.Diagnostics;

namespace LetsClip.Services.Capture;

public sealed record Segment(string Path, double Start, double End, long Bytes);

public sealed class SegmentCache
{
    private readonly Lock _gate = new();
    private readonly List<Segment> _segments = [];
    private readonly List<double> _pins = [];

    static SegmentCache()
    {
        Debug.Assert(SelfTestPasses());
    }

    public double NewestEnd
    {
        get
        {
            lock (_gate)
            {
                return _segments.Count == 0 ? 0 : _segments[^1].End;
            }
        }
    }

    public IReadOnlyList<Segment> Add(Segment segment, double keepSeconds, long byteLimit)
    {
        // Evicts By Age And Size, Never Past A Pinned Save
        lock (_gate)
        {
            _segments.Add(segment);
            var floor = _pins.Count == 0 ? double.MaxValue : _pins.Min();
            var evicted = new List<Segment>();
            var bytes = _segments.Sum(entry => entry.Bytes);
            while (_segments.Count > 1 && _segments[0].End <= floor)
            {
                var oldest = _segments[0];
                var tooOld = oldest.End < segment.End - keepSeconds;
                if (!tooOld && bytes <= byteLimit)
                {
                    break;
                }

                _segments.RemoveAt(0);
                bytes -= oldest.Bytes;
                evicted.Add(oldest);
            }

            return evicted;
        }
    }

    public IReadOnlyList<Segment> Covering(double from, double to)
    {
        lock (_gate)
        {
            return [.. _segments.Where(segment => segment.End > from && segment.Start < to)];
        }
    }

    public IDisposable Pin(double from)
    {
        lock (_gate)
        {
            _pins.Add(from);
        }

        return new PinRelease(this, from);
    }

    private void Unpin(double from)
    {
        lock (_gate)
        {
            _pins.Remove(from);
        }
    }

    private sealed class PinRelease(SegmentCache cache, double from) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                cache.Unpin(from);
            }
        }
    }

    private static bool SelfTestPasses()
    {
        // One Second Segments, A Minute Kept
        var cache = new SegmentCache();
        for (var second = 0; second < 70; second++)
        {
            cache.Add(new Segment($"{second}", second, second + 1, 100), 60, long.MaxValue);
        }

        var byAge = cache.Covering(0, 100);
        var pin = cache.Pin(20);
        var pinnedEvictions = new List<Segment>();
        for (var second = 70; second < 100; second++)
        {
            pinnedEvictions.AddRange(
                cache.Add(new Segment($"{second}", second, second + 1, 100), 60, long.MaxValue)
            );
        }

        var keptWhilePinned = cache.Covering(20, 21).Count == 1;
        pin.Dispose();
        var released = cache.Add(new Segment("100", 100, 101, 100), 60, long.MaxValue);

        // Bytes Bound Too, A Pin Still Wins
        var small = new SegmentCache();
        for (var second = 0; second < 10; second++)
        {
            small.Add(new Segment($"{second}", second, second + 1, 1000), 60, 3500);
        }

        return byAge.Count == 61
            && byAge[0].Start == 9
            && keptWhilePinned
            && pinnedEvictions.All(segment => segment.End <= 20)
            && released.Count > 0
            && released.All(segment => segment.Start < 40)
            && small.Covering(0, 10).Sum(segment => segment.Bytes) <= 3500
            && small.Covering(0, 10).Count == 3
            && small.NewestEnd == 10;
    }
}
