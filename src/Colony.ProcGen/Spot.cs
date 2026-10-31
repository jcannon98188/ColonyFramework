using Colony.Numerics;

namespace Colony.ProcGen;

/// <summary>A region-keyed spot: a position inside its region and 32 bits of per-spot randomness.</summary>
public readonly struct Spot : IEquatable<Spot>
{
    public Spot(Fixed32x2 position, uint hash)
    {
        Position = position;
        Hash = hash;
    }

    /// <summary>World position in the same units as the region size.</summary>
    public Fixed32x2 Position { get; }

    /// <summary>The spot's own random word, for choosing its kind, size or orientation without another stream.</summary>
    public uint Hash { get; }

    public bool Equals(Spot other) => Position == other.Position && Hash == other.Hash;

    public override bool Equals(object? obj) => obj is Spot other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Position, Hash);

    public static bool operator ==(Spot left, Spot right) => left.Equals(right);

    public static bool operator !=(Spot left, Spot right) => !left.Equals(right);
}

/// <summary>
/// Region-keyed spot sampling (spec section 9.3): large features are placed per region, not per chunk, so a feature
/// straddling chunks is seen identically by every chunk that asks. The spots of region (rx, ry) are drawn from the
/// counter-based RNG stream <see cref="NoiseKey.RegionSource(int, int)"/>: counter 0 is the count, then three
/// counters per spot (x, y, hash). Spot <c>i</c> is therefore a pure function of (seed, stage, region, i).
/// </summary>
public static class SpotSampler
{
    private const int CountersPerSpot = 3;

    /// <summary>
    /// Writes the spots of a region into <paramref name="output"/> and returns how many. The count is uniform in
    /// [<paramref name="minCount"/>, <paramref name="maxCount"/>] and truncated to the output length; truncation
    /// never changes the spots that are written.
    /// </summary>
    public static int Sample(NoiseKey key, int regionX, int regionY, Fixed32 regionSize, int minCount, int maxCount, Span<Spot> output)
    {
        if (minCount < 0 || maxCount < minCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "Expected 0 <= minCount <= maxCount.");
        }

        var source = key.RegionSource(regionX, regionY);
        int count = source.Int32At(0, minCount, maxCount + 1);
        count = count < output.Length ? count : output.Length;
        var origin = new Fixed32x2(Fixed32.FromInt32(regionX) * regionSize, Fixed32.FromInt32(regionY) * regionSize);
        for (int i = 0; i < count; i++)
        {
            output[i] = SpotAt(source, origin, regionSize, i);
        }

        return count;
    }

    /// <summary>Spot <paramref name="index"/> of a region, without drawing the count or the other spots.</summary>
    public static Spot SpotAt(NoiseKey key, int regionX, int regionY, Fixed32 regionSize, int index)
    {
        var origin = new Fixed32x2(Fixed32.FromInt32(regionX) * regionSize, Fixed32.FromInt32(regionY) * regionSize);
        return SpotAt(key.RegionSource(regionX, regionY), origin, regionSize, index);
    }

    private static Spot SpotAt(RandomSource source, Fixed32x2 origin, Fixed32 regionSize, int index)
    {
        ulong counter = 1 + (CountersPerSpot * (ulong)(uint)index);
        var fx = Fixed32.FromRaw((int)(source.UInt32At(counter) >> 16));
        var fy = Fixed32.FromRaw((int)(source.UInt32At(counter + 1) >> 16));
        uint hash = source.UInt32At(counter + 2);
        return new Spot(new Fixed32x2(origin.X + (regionSize * fx), origin.Y + (regionSize * fy)), hash);
    }
}
