using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// An append-only native byte run that doubles in place: the backing store of the string table and the blob
/// store. Appended bytes never move relative to their offset, so callers keep (offset, length) pairs.
/// Not thread safe; the owner serialises writes.
/// </summary>
internal sealed unsafe class NativeBytes : IDisposable
{
    private byte* _data;
    private long _capacity;
    private long _length;

    public NativeBytes(long initialCapacity)
    {
        _capacity = Math.Max(64, initialCapacity);
        _data = (byte*)NativeMemory.Alloc((nuint)_capacity);
    }

    public long Length => _length;

    /// <summary>Appends <paramref name="bytes"/> (after padding to <paramref name="alignment"/>) and returns the offset of its first byte.</summary>
    public long Append(ReadOnlySpan<byte> bytes, int alignment)
    {
        long start = (_length + alignment - 1) & ~(long)(alignment - 1);
        long end = start + bytes.Length;
        if (end > _capacity)
        {
            long capacity = _capacity;
            while (capacity < end)
            {
                capacity *= 2;
            }

            _data = (byte*)NativeMemory.Realloc(_data, (nuint)capacity);
            _capacity = capacity;
        }

        if (start > _length)
        {
            new Span<byte>(_data + _length, (int)(start - _length)).Clear();
        }

        bytes.CopyTo(new Span<byte>(_data + start, bytes.Length));
        _length = end;
        return start;
    }

    public ReadOnlySpan<byte> Slice(long offset, int length) => new(_data + offset, length);

    public void Dispose()
    {
        if (_data is not null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }
    }
}
