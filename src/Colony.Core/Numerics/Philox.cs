namespace Colony.Numerics;

/// <summary>Four 32-bit words produced by one Philox block.</summary>
public readonly struct RandomBlock : IEquatable<RandomBlock>
{
    private readonly uint _w0;
    private readonly uint _w1;
    private readonly uint _w2;
    private readonly uint _w3;

    internal RandomBlock(uint w0, uint w1, uint w2, uint w3)
    {
        _w0 = w0;
        _w1 = w1;
        _w2 = w2;
        _w3 = w3;
    }

    public uint this[int index] => index switch
    {
        0 => _w0,
        1 => _w1,
        2 => _w2,
        3 => _w3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public bool Equals(RandomBlock other) => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

    public override bool Equals(object? obj) => obj is RandomBlock other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3);

    public static bool operator ==(RandomBlock left, RandomBlock right) => left.Equals(right);

    public static bool operator !=(RandomBlock left, RandomBlock right) => !left.Equals(right);
}

/// <summary>
/// Philox4x32-10 (Salmon et al., "Parallel random numbers: as easy as 1, 2, 3"). A pure function of
/// (key, counter): the Framework addresses it with key = World seed and counter = (block counter, stream key),
/// so any consumer can draw value N of stream S without touching shared state.
/// </summary>
public static class Philox
{
    private const uint Multiplier0 = 0xD2511F53u;
    private const uint Multiplier1 = 0xCD9E8D57u;
    private const uint KeyBump0 = 0x9E3779B9u;
    private const uint KeyBump1 = 0xBB67AE85u;
    private const int Rounds = 10;

    /// <summary>
    /// Generates one block. Word layout: counter words (c0, c1) = <paramref name="counter"/> low/high,
    /// (c2, c3) = <paramref name="streamKey"/> low/high; key words (k0, k1) = <paramref name="seed"/> low/high.
    /// </summary>
    public static RandomBlock Generate(ulong seed, ulong streamKey, ulong counter)
    {
        uint c0 = (uint)counter;
        uint c1 = (uint)(counter >> 32);
        uint c2 = (uint)streamKey;
        uint c3 = (uint)(streamKey >> 32);
        uint k0 = (uint)seed;
        uint k1 = (uint)(seed >> 32);

        for (int round = 0; round < Rounds; round++)
        {
            ulong product0 = (ulong)Multiplier0 * c0;
            ulong product1 = (ulong)Multiplier1 * c2;
            uint hi0 = (uint)(product0 >> 32);
            uint lo0 = (uint)product0;
            uint hi1 = (uint)(product1 >> 32);
            uint lo1 = (uint)product1;

            c0 = hi1 ^ c1 ^ k0;
            c1 = lo1;
            c2 = hi0 ^ c3 ^ k1;
            c3 = lo0;

            k0 += KeyBump0;
            k1 += KeyBump1;
        }

        return new RandomBlock(c0, c1, c2, c3);
    }
}
