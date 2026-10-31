using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony.Numerics;

/// <summary>
/// A stream of random values addressed by (World seed, stream key, counter) with no mutable state (spec section 6).
/// Value <c>n</c> of a stream is always the same for the same seed and key, whatever else was drawn, on however
/// many threads. Derive sub-streams with <see cref="Derive(ulong)"/> or <see cref="Derive(ReadOnlySpan{char})"/>
/// (for example per System, per Entity or per chunk) so adding a consumer never shifts another consumer's values.
/// Each counter addresses one 32-bit word; 64-bit draws use an aligned pair of counters.
/// </summary>
public readonly struct RandomSource : IEquatable<RandomSource>
{
    public RandomSource(ulong seed, ulong key)
    {
        Seed = seed;
        Key = key;
    }

    /// <summary>The World seed (Philox key).</summary>
    public ulong Seed { get; }

    /// <summary>The stream key (upper Philox counter words).</summary>
    public ulong Key { get; }

    /// <summary>A sub-stream whose key mixes this key with <paramref name="subKey"/> (order sensitive).</summary>
    public RandomSource Derive(ulong subKey) => new(Seed, Hash64.Combine(Key, subKey));

    /// <summary>A sub-stream named by text; the name is hashed with <see cref="Hash64"/>, so no allocation.</summary>
    public RandomSource Derive(ReadOnlySpan<char> name) => Derive(Hash64.Compute(MemoryMarshal.AsBytes(name), 0x4E414D45UL));

    /// <summary>The Philox block holding counters <c>4 * blockIndex</c> to <c>4 * blockIndex + 3</c>.</summary>
    public RandomBlock BlockAt(ulong blockIndex) => Philox.Generate(Seed, Key, blockIndex);

    public uint UInt32At(ulong counter) => BlockAt(counter >> 2)[(int)(counter & 3)];

    /// <summary>Two consecutive words starting at the even counter at or below <paramref name="counter"/>.</summary>
    public ulong UInt64At(ulong counter)
    {
        var block = BlockAt(counter >> 2);
        int lane = (int)(counter & 2);
        return block[lane] | ((ulong)block[lane + 1] << 32);
    }

    /// <summary>Uniform in [0, 1) with 24 random bits, so every value is exactly representable and 1 never appears.</summary>
    public float SingleAt(ulong counter) => (UInt32At(counter) >> 8) * (1f / 16777216f);

    /// <summary>Uniform in [0, 1) with 53 random bits.</summary>
    public double DoubleAt(ulong counter) => (UInt64At(counter) >> 11) * (1d / 9007199254740992d);

    /// <summary>
    /// Uniform integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>) by fixed-point scaling of one
    /// 32-bit word (bias below 2^-32 relative, no rejection loop so exactly one counter is consumed).
    /// An empty or inverted range yields <paramref name="minInclusive"/>.
    /// </summary>
    public int Int32At(ulong counter, int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            return minInclusive;
        }

        ulong range = (ulong)((long)maxExclusive - minInclusive);
        ulong scaled = (UInt32At(counter) * range) >> 32;
        return (int)(minInclusive + (long)scaled);
    }

    public bool BoolAt(ulong counter) => (UInt32At(counter) & 1) != 0;

    public bool Equals(RandomSource other) => Seed == other.Seed && Key == other.Key;

    public override bool Equals(object? obj) => obj is RandomSource other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Seed, Key);

    public static bool operator ==(RandomSource left, RandomSource right) => left.Equals(right);

    public static bool operator !=(RandomSource left, RandomSource right) => !left.Equals(right);
}

/// <summary>
/// A cursor over a <see cref="RandomSource"/>: the stream plus the next counter. Unmanaged and saveable, so it
/// can live in a Component or Resource and be part of the World hash. Restarting from a saved counter reproduces
/// the continuation exactly.
/// </summary>
public struct RandomSequence : IEquatable<RandomSequence>
{
    public RandomSequence(RandomSource stream)
        : this(stream, 0)
    {
    }

    public RandomSequence(RandomSource stream, ulong counter)
    {
        Stream = stream;
        Counter = counter;
    }

    public RandomSource Stream { get; }

    /// <summary>The counter the next draw will use.</summary>
    public ulong Counter { get; private set; }

    public uint NextUInt32() => Stream.UInt32At(Counter++);

    /// <summary>Consumes an aligned pair of counters (skipping one if the counter is odd).</summary>
    public ulong NextUInt64()
    {
        Counter = (Counter + 1) & ~1UL;
        ulong value = Stream.UInt64At(Counter);
        Counter += 2;
        return value;
    }

    public float NextSingle() => Stream.SingleAt(Counter++);

    public double NextDouble()
    {
        Counter = (Counter + 1) & ~1UL;
        double value = Stream.DoubleAt(Counter);
        Counter += 2;
        return value;
    }

    public int NextInt32(int minInclusive, int maxExclusive) => Stream.Int32At(Counter++, minInclusive, maxExclusive);

    public bool NextBool() => Stream.BoolAt(Counter++);

    /// <summary>Advances the counter without drawing.</summary>
    public void Skip(ulong count) => Counter += count;

    public bool Equals(RandomSequence other) => Stream == other.Stream && Counter == other.Counter;

    public override bool Equals(object? obj) => obj is RandomSequence other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Stream, Counter);

    public static bool operator ==(RandomSequence left, RandomSequence right) => left.Equals(right);

    public static bool operator !=(RandomSequence left, RandomSequence right) => !left.Equals(right);
}
