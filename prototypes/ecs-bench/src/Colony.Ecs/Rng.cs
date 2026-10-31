using System.Numerics;
using System.Runtime.CompilerServices;

namespace Colony.Ecs;

/// <summary>Philox4x32-10 counter-based RNG. Stateless: same (key, counter) always gives the same output.</summary>
public static class Philox
{
    public struct Block
    {
        public uint A, B, C, D;
    }

    private const uint M0 = 0xD2511F53, M1 = 0xCD9E8D57, W0 = 0x9E3779B9, W1 = 0xBB67AE85;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Block Generate(ulong key, uint c0, uint c1, uint c2, uint c3)
    {
        uint k0 = (uint)key, k1 = (uint)(key >> 32);
        for (int i = 0; i < 10; i++)
        {
            ulong p0 = (ulong)M0 * c0;
            ulong p1 = (ulong)M1 * c2;
            uint n0 = (uint)(p1 >> 32) ^ c1 ^ k0;
            uint n1 = (uint)p1;
            uint n2 = (uint)(p0 >> 32) ^ c3 ^ k1;
            uint n3 = (uint)p0;
            c0 = n0; c1 = n1; c2 = n2; c3 = n3;
            k0 += W0; k1 += W1;
        }
        return new Block { A = c0, B = c1, C = c2, D = c3 };
    }

    /// <summary>Keyed by (seed, system id, entity, tick, stream). No hidden state, so order of calls across threads is irrelevant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Block For(ulong seed, int systemId, Entity e, long tick, uint stream = 0)
        => Generate(seed, (uint)e.Index, e.Generation, (uint)tick, ((uint)systemId << 16) ^ stream ^ (uint)((ulong)tick >> 32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Block For(ulong seed, int systemId, uint index, long tick, uint stream = 0)
        => Generate(seed, index, 0xFFFFFFFFu, (uint)tick, ((uint)systemId << 16) ^ stream);
}

/// <summary>One-shot xxHash64 (seeded), chained by passing the previous hash as the seed.</summary>
public static unsafe class XxHash64
{
    private const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
        P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + input * P2, 31) * P1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Merge(ulong acc, ulong val) => (acc ^ Round(0, val)) * P1 + P4;

    public static ulong Hash(byte* p, int len, ulong seed)
    {
        byte* end = p + len;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            byte* limit = end - 32;
            do
            {
                v1 = Round(v1, *(ulong*)p); p += 8;
                v2 = Round(v2, *(ulong*)p); p += 8;
                v3 = Round(v3, *(ulong*)p); p += 8;
                v4 = Round(v4, *(ulong*)p); p += 8;
            } while (p <= limit);
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1); h = Merge(h, v2); h = Merge(h, v3); h = Merge(h, v4);
        }
        else h = seed + P5;
        h += (ulong)len;
        while (p + 8 <= end) { h ^= Round(0, *(ulong*)p); h = BitOperations.RotateLeft(h, 27) * P1 + P4; p += 8; }
        if (p + 4 <= end) { h ^= *(uint*)p * P1; h = BitOperations.RotateLeft(h, 23) * P2 + P3; p += 4; }
        while (p < end) { h ^= *p * P5; h = BitOperations.RotateLeft(h, 11) * P1; p++; }
        h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
        return h;
    }
}
