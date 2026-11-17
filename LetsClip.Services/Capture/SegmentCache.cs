using System.Diagnostics;

namespace LetsClip.Services.Capture;

public sealed class Segment(string path, double start, double end, byte[] data)
{
    // In Memory Until Spilled To Its File
    private byte[]? _buffer = data;
    private volatile bool _spilled;

    public string Path { get; } = path;

    public double Start { get; } = start;

    public double End { get; } = end;

    public long Bytes { get; } = data.Length;

    public bool InMemory => Volatile.Read(ref _buffer) is not null;

    public void Spill()
    {
        // Written Whole Before Memory Lets Go, Readers Always Find One
        if (Volatile.Read(ref _buffer) is not { } held)
        {
            return;
        }

        using (var file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            file.Write(held);
        }

        _spilled = true;
        Volatile.Write(ref _buffer, null);
    }

    public void Release()
    {
        // A Spilled Copy Goes With It
        Volatile.Write(ref _buffer, null);
        if (_spilled)
        {
            File.Delete(Path);
        }
    }

    public async Task CopyToAsync(Stream target, CancellationToken token)
    {
        if (Volatile.Read(ref _buffer) is { } held)
        {
            await target.WriteAsync(held, token);
            return;
        }

        await using var file = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        await file.CopyToAsync(target, token);
    }
}

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

    public IReadOnlyList<Segment> OverMemory(long memoryLimit)
    {
        // The Oldest Past The Budget Go To Disk, Newest Stay
        lock (_gate)
        {
            var held = _segments.Where(segment => segment.InMemory).Sum(segment => segment.Bytes);
            var spills = new List<Segment>();
            foreach (var segment in _segments.Where(segment => segment.InMemory))
            {
                if (held <= memoryLimit)
                {
                    break;
                }

                spills.Add(segment);
                held -= segment.Bytes;
            }

            return spills;
        }
    }

    public IReadOnlyList<Segment> Covering(double from, double to)
    {
        lock (_gate)
        {
            return [.. _segments.Where(segment => segment.End > from && segment.Start < to)];
        }
    }

    public void Pin(double from)
    {
        lock (_gate)
        {
            _pins.Add(from);
        }
    }

    public void Unpin(double from)
    {
        lock (_gate)
        {
            _pins.Remove(from);
        }
    }

    private static bool SelfTestPasses()
    {
        // One Second Segments, A Minute Kept
        var cache = new SegmentCache();
        for (var second = 0; second < 70; second++)
        {
            cache.Add(
                new Segment($"{second}", second, second + 1, new byte[100]),
                60,
                long.MaxValue
            );
        }

        var byAge = cache.Covering(0, 100);
        cache.Pin(20);
        var pinnedEvictions = new List<Segment>();
        for (var second = 70; second < 100; second++)
        {
            pinnedEvictions.AddRange(
                cache.Add(
                    new Segment($"{second}", second, second + 1, new byte[100]),
                    60,
                    long.MaxValue
                )
            );
        }

        var keptWhilePinned = cache.Covering(20, 21).Count == 1;
        var spills = cache.OverMemory(6000);
        cache.Unpin(20);
        var released = cache.Add(new Segment("100", 100, 101, new byte[100]), 60, long.MaxValue);

        // Bytes Bound Too, A Pin Still Wins
        var small = new SegmentCache();
        for (var second = 0; second < 10; second++)
        {
            small.Add(new Segment($"{second}", second, second + 1, new byte[1000]), 60, 3500);
        }

        return byAge.Count == 61
            && byAge[0].Start == 9
            && keptWhilePinned
            && pinnedEvictions.All(segment => segment.End <= 20)
            && spills.Count == 20
            && spills[0].Start == 20
            && released.Count > 0
            && released.All(segment => segment.Start < 40)
            && small.Covering(0, 10).Sum(segment => segment.Bytes) <= 3500
            && small.Covering(0, 10).Count == 3
            && small.NewestEnd == 10;
    }
}
