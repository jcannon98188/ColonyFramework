namespace Colony.ProcGen;

/// <summary>
/// The integer lattice hash behind every noise function: the coordinate words are spread with odd multipliers,
/// combined with the two key words from <see cref="Colony.Hashing.Hash64.Combine(ulong, ulong)"/>(seed, stage),
/// and avalanched twice with the MurmurHash3 32-bit finaliser. Everything is 32-bit integer arithmetic so the same
/// code runs on scalar and SIMD int32 lanes (the noise kernels) and is trivially bit-identical.
/// The scalar form here is the reference; the lane form must match it bit for bit.
/// </summary>
internal static class LatticeHash
{
    internal const uint XMultiplier = 0x9E3779B1u; // 2^32 / golden ratio, odd
    internal const uint YMultiplier = 0x85EBCA77u;
    internal const uint ZMultiplier = 0xC2B2AE3Du;
    internal const uint Dimension3Salt = 0x27D4EB2Fu;
    internal const uint Fmix1 = 0x85EBCA6Bu;
    internal const uint Fmix2 = 0xC2B2AE35u;

    internal static uint Hash2(uint keyLow, uint keyHigh, int x, int y)
    {
        uint h = unchecked(((uint)x * XMultiplier) ^ ((uint)y * YMultiplier) ^ keyLow);
        h = Fmix(h);
        h = Fmix(h ^ keyHigh);
        return h;
    }

    internal static uint Hash3(uint keyLow, uint keyHigh, int x, int y, int z)
    {
        uint h = unchecked(((uint)x * XMultiplier) ^ ((uint)y * YMultiplier) ^ ((uint)z * ZMultiplier) ^ keyLow ^ Dimension3Salt);
        h = Fmix(h);
        h = Fmix(h ^ keyHigh);
        return h;
    }

    /// <summary>MurmurHash3 fmix32.</summary>
    internal static uint Fmix(uint h)
    {
        unchecked
        {
            h ^= h >> 16;
            h *= Fmix1;
            h ^= h >> 13;
            h *= Fmix2;
            h ^= h >> 16;
        }

        return h;
    }
}
