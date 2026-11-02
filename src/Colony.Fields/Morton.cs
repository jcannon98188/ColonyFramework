namespace Colony.Fields;

/// <summary>
/// 3D Morton (Z-order) codes: 21 bits per axis interleaved into 63 bits (x in bit 0, y in bit 1, z in
/// bit 2, then the next bit of each). Chunk coordinates stay 64-bit-safe so unbounded Worlds can be
/// added later (spec section 5.3).
/// </summary>
public static class Morton
{
    /// <summary>Largest coordinate per axis: 2^21 - 1.</summary>
    public const int MaxCoordinate = (1 << 21) - 1;

    public static ulong Encode(int x, int y, int z)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(x, MaxCoordinate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(y, MaxCoordinate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(z, MaxCoordinate);
        return Spread((uint)x) | (Spread((uint)y) << 1) | (Spread((uint)z) << 2);
    }

    public static (int X, int Y, int Z) Decode(ulong code)
        => ((int)Compact(code), (int)Compact(code >> 1), (int)Compact(code >> 2));

    /// <summary>Puts the low 21 bits of <paramref name="v"/> at every third bit position.</summary>
    private static ulong Spread(uint v)
    {
        ulong x = v & 0x1F_FFFF;
        x = (x | (x << 32)) & 0x001F_0000_0000_FFFF;
        x = (x | (x << 16)) & 0x001F_0000_FF00_00FF;
        x = (x | (x << 8)) & 0x100F_00F0_0F00_F00F;
        x = (x | (x << 4)) & 0x10C3_0C30_C30C_30C3;
        x = (x | (x << 2)) & 0x1249_2492_4924_9249;
        return x;
    }

    private static ulong Compact(ulong x)
    {
        x &= 0x1249_2492_4924_9249;
        x = (x ^ (x >> 2)) & 0x10C3_0C30_C30C_30C3;
        x = (x ^ (x >> 4)) & 0x100F_00F0_0F00_F00F;
        x = (x ^ (x >> 8)) & 0x001F_0000_FF00_00FF;
        x = (x ^ (x >> 16)) & 0x001F_0000_0000_FFFF;
        x = (x ^ (x >> 32)) & 0x1F_FFFF;
        return x;
    }
}
