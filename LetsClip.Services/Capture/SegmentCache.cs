using System.Buffers;
using System.Diagnostics;

namespace LetsClip.Services.Capture;

public sealed class Segment
{
    // In Native Memory Until Spilled To Its File
    private readonly Lock _gate = new();
    private SegmentBuffer? _buffer;
    private bool _spilled;

    public Segment(string path, double start, double end, SegmentBuffer data)
    {
        (Path, Start, End, Bytes, _buffer) = (path, start, end, data.Length, data);
        SegmentMemory.Count(Bytes);
    }

    public string Path { get; }

    public double Start { get; }

    public double End { get; }

    public long Bytes { get; }

    // Arrival Order Across Sessions, The Oldest Spill First
    public long Arrived { get; } = Stopwatch.GetTimestamp();

    public bool InMemory
    {
        get
        {
            lock (_gate)
            {
                return _buffer is not null;
            }
        }
    }

    public void Spill()
    {
        // Written Whole Before Memory Lets Go, Readers Always Find One
        lock (_gate)
        {
            if (_buffer is not { } held)
            {
                return;
            }

            using (
                var file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read)
            )
            {
                file.Write(held.Read(0, held.Length));
            }

            _spilled = true;
            LetGo(held);
        }
    }

    public void Release()
    {
        // A Spilled Copy Goes With It
        lock (_gate)
        {
            if (_buffer is { } held)
            {
                LetGo(held);
            }

            if (_spilled)
            {
                File.Delete(Path);
                _spilled = false;
            }
        }
    }

    private void LetGo(SegmentBuffer held)
    {
        // A Save Still Copying Keeps It Until Done
        _buffer = null;
        SegmentMemory.Count(-Bytes);
        held.Dispose();
    }

    public async Task CopyToAsync(Stream target, CancellationToken token)
    {
        SegmentBuffer? held;
        var added = false;
        lock (_gate)
        {
            held = _buffer;
            held?.DangerousAddRef(ref added);
        }

        if (held is null)
        {
            await using var file = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            await file.CopyToAsync(target, token);
            return;
        }

        // Out Of Native Memory Through One Pooled Chunk
        var chunk = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            for (var offset = 0; offset < held.Length; offset += chunk.Length)
            {
                var count = Math.Min(chunk.Length, held.Length - offset);
                held.Read(offset, count).CopyTo(chunk);
                await target.WriteAsync(chunk.AsMemory(0, count), token);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            if (added)
            {
                held.DangerousRelease();
            }
        }
    }
}

public sealed class SegmentCache
{
    private readonly Lock _gate = new();
    private readonly List<Segment> _segments = [];
    private readonly List<double> _pins = [];
    private long _bytes;

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
            _bytes += segment.Bytes;
            var floor = _pins.Count == 0 ? double.MaxValue : _pins.Min();
            var evicted = new List<Segment>();
            while (_segments.Count > 1 && _segments[0].End <= floor)
            {
                var oldest = _segments[0];
                var tooOld = oldest.End < segment.End - keepSeconds;
                if (!tooOld && _bytes <= byteLimit)
                {
                    break;
                }

                _segments.RemoveAt(0);
                _bytes -= oldest.Bytes;
                evicted.Add(oldest);
            }

            return evicted;
        }
    }

    public IReadOnlyList<Segment> InMemory()
    {
        lock (_gate)
        {
            return [.. _segments.Where(segment => segment.InMemory)];
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

    public void ReleaseAll()
    {
        // Memory And Spilled Files Go, The Cache Leaves The Budget
        List<Segment> released;
        lock (_gate)
        {
            released = [.. _segments];
            _segments.Clear();
            _bytes = 0;
        }

        SegmentMemory.Unregister(this);
        foreach (var segment in released)
        {
            try
            {
                segment.Release();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A File Still Read Goes With The Session Folder
            }
        }
    }

    private static bool SelfTestPasses()
    {
        // One Second Segments, A Minute Kept
        var cache = new SegmentCache();
        var held = SegmentMemory.Held;
        static Segment SegmentAt(int second, int bytes = 100) =>
            new($"{second}", second, second + 1, SegmentBuffer.Of(new byte[bytes]));
        var aged = new List<Segment>();
        for (var second = 0; second < 70; second++)
        {
            aged.AddRange(cache.Add(SegmentAt(second), 60, long.MaxValue));
        }

        var byAge = cache.Covering(0, 100);
        cache.Pin(20);
        var pinnedEvictions = new List<Segment>();
        for (var second = 70; second < 100; second++)
        {
            pinnedEvictions.AddRange(cache.Add(SegmentAt(second), 60, long.MaxValue));
        }

        var keptWhilePinned = cache.Covering(20, 21).Count == 1;
        cache.Unpin(20);
        var released = cache.Add(SegmentAt(100), 60, long.MaxValue);

        // Bytes Bound Too, A Pin Still Wins
        var small = new SegmentCache();
        var smallEvicted = new List<Segment>();
        for (var second = 0; second < 10; second++)
        {
            smallEvicted.AddRange(small.Add(SegmentAt(second, 1000), 60, 3500));
        }

        var passes =
            byAge.Count == 61
            && byAge[0].Start == 9
            && keptWhilePinned
            && pinnedEvictions.All(segment => segment.End <= 20)
            && released.Count > 0
            && released.All(segment => segment.Start < 40)
            && small.Covering(0, 10).Sum(segment => segment.Bytes) <= 3500
            && small.Covering(0, 10).Count == 3
            && small.NewestEnd == 10
            && small.InMemory().Count == 3;

        // Native Memory Counted In And Out Again
        foreach (var evicted in aged.Concat(pinnedEvictions).Concat(released).Concat(smallEvicted))
        {
            evicted.Release();
        }

        cache.ReleaseAll();
        small.ReleaseAll();
        return passes && SegmentMemory.Held == held;
    }
}
