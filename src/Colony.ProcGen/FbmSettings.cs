using Colony.Numerics;

namespace Colony.ProcGen;

/// <summary>The scalar noise basis an fBm sum is built from.</summary>
public enum NoiseBasis
{
    Value,
    Gradient,
    Simplex,
}

/// <summary>
/// Fractal Brownian motion parameters. Octave <c>o</c> samples the basis with key <c>key.Derive(o)</c> at frequency
/// <c>Lacunarity^o</c> and amplitude <c>Gain^o</c>; the sum is divided by the total amplitude so the result stays
/// in [-1, 1]. Construct with <c>new FbmSettings { ... }</c> (the defaults are 4 octaves, lacunarity 2, gain 0.5,
/// gradient basis); <c>default(FbmSettings)</c> has zero octaves and is rejected.
/// </summary>
public readonly struct FbmSettings : IEquatable<FbmSettings>
{
    public const int MaxOctaves = 16;

    public FbmSettings()
    {
        Octaves = 4;
        Lacunarity = Fixed32.FromInt32(2);
        Gain = Fixed32.Half;
        Basis = NoiseBasis.Gradient;
    }

    /// <summary>Number of octaves, 1 to <see cref="MaxOctaves"/>.</summary>
    public int Octaves { get; init; }

    /// <summary>Frequency multiplier per octave.</summary>
    public Fixed32 Lacunarity { get; init; }

    /// <summary>Amplitude multiplier per octave.</summary>
    public Fixed32 Gain { get; init; }

    public NoiseBasis Basis { get; init; }

    public bool Equals(FbmSettings other) => Octaves == other.Octaves && Lacunarity == other.Lacunarity && Gain == other.Gain && Basis == other.Basis;

    public override bool Equals(object? obj) => obj is FbmSettings other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Octaves, Lacunarity.Raw, Gain.Raw, (int)Basis);

    public static bool operator ==(FbmSettings left, FbmSettings right) => left.Equals(right);

    public static bool operator !=(FbmSettings left, FbmSettings right) => !left.Equals(right);

    internal void Validate()
    {
        if (Octaves < 1 || Octaves > MaxOctaves)
        {
            throw new ArgumentOutOfRangeException(nameof(Octaves), Octaves, $"Octaves must be between 1 and {MaxOctaves}.");
        }
    }
}
