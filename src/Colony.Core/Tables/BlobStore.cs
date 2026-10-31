using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony;

/// <summary>
/// The World's immutable blob store (spec section 4.1): <see cref="Store{T}"/> copies a span of unmanaged values
/// into World-owned memory and returns a dense <see cref="BlobRef{T}"/> in store order; <see cref="Get{T}"/>
/// reads it back as a read-only span. Blobs never change or go away, so a ref stays valid for the World's life
/// and the store is saved World state (part of <see cref="World.Hash"/>, in id order). Storing is serial
/// (outside a Tick or from a serial hook); reading is safe from chunk jobs while nothing stores. Reading
/// allocates nothing.
/// </summary>
public sealed class BlobStore : IDisposable
{
    private const int Alignment = 16;
    private readonly NativeBytes _bytes = new(4096);
    private long[] _offsets = new long[32];
    private int[] _lengths = new int[32];      // element count
    private int[] _elementSizes = new int[32];
    private int _count;

    internal BlobStore()
    {
    }

    /// <summary>Blobs stored so far; the next id is <c>Count + 1</c>.</summary>
    public int Count => _count;

    /// <summary>Total payload bytes stored (padding excluded).</summary>
    public long Bytes { get; private set; }

    /// <summary>Copies <paramref name="data"/> into the store and returns its ref. The caller's span can change afterwards; the blob cannot.</summary>
    public BlobRef<T> Store<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        if (_count == _offsets.Length)
        {
            Array.Resize(ref _offsets, _count * 2);
            Array.Resize(ref _lengths, _count * 2);
            Array.Resize(ref _elementSizes, _count * 2);
        }

        var bytes = MemoryMarshal.AsBytes(data);
        _offsets[_count] = _bytes.Append(bytes, Alignment);
        _lengths[_count] = data.Length;
        _elementSizes[_count] = Unsafe.SizeOf<T>();
        Bytes += bytes.Length;
        _count++;
        return new BlobRef<T>((uint)_count);
    }

    /// <summary>The elements of <paramref name="blob"/>; empty for a null ref, throws for an unknown id or a ref whose element size differs from the stored one.</summary>
    public ReadOnlySpan<T> Get<T>(BlobRef<T> blob)
        where T : unmanaged
    {
        if (blob.IsNull)
        {
            return default;
        }

        if (blob.Id > (uint)_count)
        {
            throw new ArgumentException($"{blob} was not issued by this blob store ({_count} blobs).", nameof(blob));
        }

        int index = (int)blob.Id - 1;
        if (_elementSizes[index] != Unsafe.SizeOf<T>())
        {
            throw new ArgumentException($"{blob} holds {_elementSizes[index]} byte elements, not {Unsafe.SizeOf<T>()} byte {typeof(T).Name}.", nameof(blob));
        }

        return MemoryMarshal.Cast<byte, T>(_bytes.Slice(_offsets[index], _lengths[index] * _elementSizes[index]));
    }

    /// <summary>Appends every blob in id order: the saved form of the store.</summary>
    internal void Hash(ref Hash64State state)
    {
        state.Append(_count);
        for (int i = 0; i < _count; i++)
        {
            state.Append(_elementSizes[i]);
            state.Append(_lengths[i]);
            state.Append(_bytes.Slice(_offsets[i], _lengths[i] * _elementSizes[i]));
        }
    }

    public void Dispose() => _bytes.Dispose();
}
