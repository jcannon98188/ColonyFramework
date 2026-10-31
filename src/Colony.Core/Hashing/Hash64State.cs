using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony.Hashing;

/// <summary>
/// Incremental <see cref="Hash64"/> (XXH64): append any number of byte runs and read the digest at any point.
/// A plain unmanaged struct (no heap buffer), so it can live in a Component, a Resource or on the stack.
/// Appending the same bytes in any split produces the same digest as <see cref="Hash64.Compute(ReadOnlySpan{byte}, ulong)"/>.
/// </summary>
public struct Hash64State : IEquatable<Hash64State>
{
    private const int StripeSize = 32;

    private ulong _v1;
    private ulong _v2;
    private ulong _v3;
    private ulong _v4;
    private ulong _length;
    private Stripe _buffer;
    private int _buffered;
    private readonly ulong _seed;

    [InlineArray(StripeSize)]
    private struct Stripe
    {
        private byte _element0;
    }

    public Hash64State(ulong seed)
    {
        _seed = seed;
        _v1 = seed + Hash64.Prime1 + Hash64.Prime2;
        _v2 = seed + Hash64.Prime2;
        _v3 = seed;
        _v4 = seed - Hash64.Prime1;
    }

    /// <summary>Total number of bytes appended so far.</summary>
    public readonly ulong Length => _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        _length += (ulong)data.Length;

        if (_buffered > 0)
        {
            int take = StripeSize - _buffered;
            if (data.Length < take)
            {
                data.CopyTo(((Span<byte>)_buffer)[_buffered..]);
                _buffered += data.Length;
                return;
            }

            data[..take].CopyTo(((Span<byte>)_buffer)[_buffered..]);
            ConsumeStripe(_buffer);
            _buffered = 0;
            data = data[take..];
        }

        while (data.Length >= StripeSize)
        {
            ConsumeStripe(data[..StripeSize]);
            data = data[StripeSize..];
        }

        if (data.Length > 0)
        {
            data.CopyTo(_buffer);
            _buffered = data.Length;
        }
    }

    /// <summary>Appends the in-memory bytes of a span of unmanaged values.</summary>
    public void Append<T>(ReadOnlySpan<T> data)
        where T : unmanaged
        => Append(MemoryMarshal.AsBytes(data));

    /// <summary>Appends the in-memory bytes of one unmanaged value.</summary>
    public void Append<T>(in T value)
        where T : unmanaged
        => Append(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>The digest of everything appended so far; the state is unchanged and can keep accepting data.</summary>
    public readonly ulong Finish()
    {
        ulong hash = _length >= StripeSize
            ? Hash64.MergeAccumulators(_v1, _v2, _v3, _v4)
            : _seed + Hash64.Prime5;
        hash += _length;
        return Hash64.Finalize(hash, ((ReadOnlySpan<byte>)_buffer)[.._buffered]);
    }

    /// <summary>Two states are equal when they were seeded alike and have absorbed the same bytes.</summary>
    public readonly bool Equals(Hash64State other)
        => _seed == other._seed && _length == other._length && _v1 == other._v1 && _v2 == other._v2 && _v3 == other._v3 && _v4 == other._v4
           && ((ReadOnlySpan<byte>)_buffer)[.._buffered].SequenceEqual(((ReadOnlySpan<byte>)other._buffer)[..other._buffered]);

    public override readonly bool Equals(object? obj) => obj is Hash64State other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_seed, _length, Finish());

    public static bool operator ==(Hash64State left, Hash64State right) => left.Equals(right);

    public static bool operator !=(Hash64State left, Hash64State right) => !left.Equals(right);

    private void ConsumeStripe(ReadOnlySpan<byte> stripe)
    {
        _v1 = Hash64.Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(stripe));
        _v2 = Hash64.Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(stripe[8..]));
        _v3 = Hash64.Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(stripe[16..]));
        _v4 = Hash64.Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(stripe[24..]));
    }
}
