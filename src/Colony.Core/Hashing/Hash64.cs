using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Colony.Hashing;

/// <summary>
/// The Framework hash primitive for chunk and World hashes (spec section 6): the 64-bit xxHash algorithm (XXH64),
/// implemented here without reflection or allocation. One-shot forms live on this type; the incremental form is
/// <see cref="Hash64State"/>. Typed overloads hash the in-memory little-endian bytes of unmanaged values, which
/// matches what a Component column or Field chunk looks like in a save.
/// </summary>
public static class Hash64
{
    internal const ulong Prime1 = 11400714785074694791UL;
    internal const ulong Prime2 = 14029467366897019727UL;
    internal const ulong Prime3 = 1609587929392839161UL;
    internal const ulong Prime4 = 9650029242287828579UL;
    internal const ulong Prime5 = 2870177450012600261UL;

    public static ulong Compute(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        ulong hash;
        int offset = 0;
        if (data.Length >= 32)
        {
            ulong v1 = seed + Prime1 + Prime2;
            ulong v2 = seed + Prime2;
            ulong v3 = seed;
            ulong v4 = seed - Prime1;
            for (; offset + 32 <= data.Length; offset += 32)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 16)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 24)..]));
            }

            hash = MergeAccumulators(v1, v2, v3, v4);
        }
        else
        {
            hash = seed + Prime5;
        }

        hash += (ulong)data.Length;
        return Finalize(hash, data[offset..]);
    }

    /// <summary>Hashes the in-memory bytes of a span of unmanaged values.</summary>
    public static ulong Compute<T>(ReadOnlySpan<T> data, ulong seed = 0)
        where T : unmanaged
        => Compute(MemoryMarshal.AsBytes(data), seed);

    /// <summary>Hashes the in-memory bytes of one unmanaged value.</summary>
    public static ulong Compute<T>(in T value, ulong seed = 0)
        where T : unmanaged
        => Compute(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)), seed);

    /// <summary>
    /// Mixes a value into an existing hash (order sensitive, avalanching). Suitable for folding chunk hashes
    /// into a World hash or deriving keys; not equal to hashing the concatenated bytes.
    /// </summary>
    public static ulong Combine(ulong hash, ulong value)
    {
        ulong mixed = hash ^ Round(0, value);
        mixed = (mixed * Prime1) + Prime4;
        return Avalanche(mixed);
    }

    internal static ulong Round(ulong accumulator, ulong input)
    {
        accumulator += input * Prime2;
        accumulator = BitOperations.RotateLeft(accumulator, 31);
        return accumulator * Prime1;
    }

    internal static ulong MergeAccumulators(ulong v1, ulong v2, ulong v3, ulong v4)
    {
        ulong hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
        hash = MergeRound(hash, v1);
        hash = MergeRound(hash, v2);
        hash = MergeRound(hash, v3);
        return MergeRound(hash, v4);
    }

    private static ulong MergeRound(ulong hash, ulong value)
    {
        hash ^= Round(0, value);
        return (hash * Prime1) + Prime4;
    }

    /// <summary>Consumes the final (fewer than 32) bytes and avalanches. <paramref name="hash"/> must already include the length.</summary>
    internal static ulong Finalize(ulong hash, ReadOnlySpan<byte> tail)
    {
        int offset = 0;
        for (; offset + 8 <= tail.Length; offset += 8)
        {
            ulong k1 = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(tail[offset..]));
            hash ^= k1;
            hash = (BitOperations.RotateLeft(hash, 27) * Prime1) + Prime4;
        }

        if (offset + 4 <= tail.Length)
        {
            hash ^= BinaryPrimitives.ReadUInt32LittleEndian(tail[offset..]) * Prime1;
            hash = (BitOperations.RotateLeft(hash, 23) * Prime2) + Prime3;
            offset += 4;
        }

        for (; offset < tail.Length; offset++)
        {
            hash ^= tail[offset] * Prime5;
            hash = BitOperations.RotateLeft(hash, 11) * Prime1;
        }

        return Avalanche(hash);
    }

    internal static ulong Avalanche(ulong hash)
    {
        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        hash ^= hash >> 32;
        return hash;
    }
}
