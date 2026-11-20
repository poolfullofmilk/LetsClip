using System.Runtime.InteropServices;

namespace LetsClip.Services.Capture;

public sealed unsafe class SegmentBuffer : SafeHandle
{
    private SegmentBuffer(byte* memory, int length)
        : base(0, ownsHandle: true)
    {
        // Native Memory, Freed Once Its Last Reader Lets Go
        SetHandle((nint)memory);
        Length = length;
    }

    public int Length { get; }

    public override bool IsInvalid => handle == 0;

    public static SegmentBuffer Of(ReadOnlySpan<byte> bytes)
    {
        var memory = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        return new SegmentBuffer(memory, bytes.Length);
    }

    internal static SegmentBuffer Adopt(byte* memory, int length)
    {
        return new SegmentBuffer(memory, length);
    }

    public ReadOnlySpan<byte> Read(int offset, int count)
    {
        return new ReadOnlySpan<byte>((byte*)handle + offset, count);
    }

    protected override bool ReleaseHandle()
    {
        NativeMemory.Free((void*)handle);
        return true;
    }
}

public sealed unsafe class SegmentBuilder(int capacity) : IDisposable
{
    // A Segment Arriving, Grown In Native Memory, Read By Saves
    private readonly Lock _gate = new();
    private byte* _memory = (byte*)NativeMemory.Alloc((nuint)Math.Max(capacity, 1));
    private int _capacity = Math.Max(capacity, 1);

    public int Length { get; private set; }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (Length + bytes.Length > _capacity)
            {
                _capacity = Math.Max(_capacity * 2, Length + bytes.Length);
                _memory = (byte*)NativeMemory.Realloc(_memory, (nuint)_capacity);
            }

            bytes.CopyTo(new Span<byte>(_memory + Length, bytes.Length));
            Length += bytes.Length;
        }
    }

    public SegmentBuffer? Copy()
    {
        // What Arrived So Far, A Still Window's Open Segment
        lock (_gate)
        {
            return _memory is null || Length == 0
                ? null
                : SegmentBuffer.Of(new ReadOnlySpan<byte>(_memory, Length));
        }
    }

    public SegmentBuffer? Finish()
    {
        // Trimmed To Size And Handed Over, Empty Ones Kept Out
        lock (_gate)
        {
            if (_memory is null || Length == 0)
            {
                return null;
            }

            var memory = (byte*)NativeMemory.Realloc(_memory, (nuint)Length);
            _memory = null;
            return SegmentBuffer.Adopt(memory, Length);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            NativeMemory.Free(_memory);
            _memory = null;
        }

        GC.SuppressFinalize(this);
    }
}
