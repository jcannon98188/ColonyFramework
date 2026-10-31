using System.Runtime.InteropServices;
using Colony.Hashing;
using Colony.Numerics;

namespace Colony.ProcGen;

/// <summary>
/// The (World seed, generation stage) half of the generation randomness key (spec section 9.3). Every noise
/// function and spot sampler takes a key plus a coordinate (and, for spots, a use index) and nothing else,
/// so any chunk evaluated alone, in any order, on any thread yields the same bits, and adding a stage or a
/// consumer never shifts another's numbers. Keys are 24 bytes of plain data; derive one per stage with
/// <see cref="Create(ulong, ReadOnlySpan{char})"/> and one per independent use (for example per fBm octave)
/// with <see cref="Derive(ulong)"/>.
/// </summary>
public readonly struct NoiseKey : IEquatable<NoiseKey>
{
    private const ulong StageNameSeed = 0x5354414745UL; // "STAGE"

    private readonly uint _mixLow;
    private readonly uint _mixHigh;

    public NoiseKey(ulong seed, ulong stage)
    {
        Seed = seed;
        Stage = stage;
        ulong mix = Hash64.Combine(seed, stage);
        _mixLow = (uint)mix;
        _mixHigh = (uint)(mix >> 32);
    }

    /// <summary>The World seed.</summary>
    public ulong Seed { get; }

    /// <summary>The stage key: <see cref="Hash64"/> of the stage name, or a derived use index.</summary>
    public ulong Stage { get; }

    /// <summary>The two 32-bit lattice mixing words, <see cref="Hash64.Combine(ulong, ulong)"/> of (seed, stage).</summary>
    internal uint MixLow => _mixLow;

    internal uint MixHigh => _mixHigh;

    /// <summary>A key for a named stage. The name is hashed with <see cref="Hash64"/>, so no allocation.</summary>
    public static NoiseKey Create(ulong seed, ReadOnlySpan<char> stageName) => new(seed, Hash64.Compute(MemoryMarshal.AsBytes(stageName)));

    /// <summary>An independent key for one use of this stage (an fBm octave, a warp axis, a sub-feature).</summary>
    public NoiseKey Derive(ulong useIndex) => new(Seed, Hash64.Combine(Stage, useIndex));

    /// <summary>
    /// The lattice hash of an integer 2D coordinate: 32 well-mixed bits that are a pure function of
    /// (seed, stage, x, y). This is what every noise function samples at lattice points.
    /// </summary>
    public uint Hash(int x, int y) => LatticeHash.Hash2(_mixLow, _mixHigh, x, y);

    /// <summary>The lattice hash of an integer 3D coordinate; never equal to the 2D hash of (x, y) by construction.</summary>
    public uint Hash(int x, int y, int z) => LatticeHash.Hash3(_mixLow, _mixHigh, x, y, z);

    /// <summary>
    /// The counter-based RNG stream for a region coordinate: (seed, Hash64(stage, region)) addressed by use index.
    /// Region-keyed placement draws value <c>n</c> of this stream, so it is independent of neighbouring regions and
    /// of every other stage.
    /// </summary>
    public RandomSource RegionSource(int regionX, int regionY)
        => new(Seed, Hash64.Combine(Stage, ((ulong)(uint)regionX << 32) | (uint)regionY));

    /// <summary>The counter-based RNG stream for a 3D region coordinate.</summary>
    public RandomSource RegionSource(int regionX, int regionY, int regionZ)
        => new(Seed, Hash64.Combine(Hash64.Combine(Stage, ((ulong)(uint)regionX << 32) | (uint)regionY), (uint)regionZ));

    public bool Equals(NoiseKey other) => Seed == other.Seed && Stage == other.Stage;

    public override bool Equals(object? obj) => obj is NoiseKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Seed, Stage);

    public static bool operator ==(NoiseKey left, NoiseKey right) => left.Equals(right);

    public static bool operator !=(NoiseKey left, NoiseKey right) => !left.Equals(right);
}
