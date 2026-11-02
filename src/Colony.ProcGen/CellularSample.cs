using Colony.Numerics;

namespace Colony.ProcGen;

/// <summary>One cellular noise sample: the nearest (F1) and second-nearest (F2) feature distances and the nearest cell's hash.</summary>
public readonly struct CellularSample : IEquatable<CellularSample>
{
    public CellularSample(Fixed32 f1, Fixed32 f2, uint cell)
    {
        F1 = f1;
        F2 = f2;
        Cell = cell;
    }

    /// <summary>Distance to the nearest feature point, in lattice units (quantum 2^-14, saturates at sqrt(8)).</summary>
    public Fixed32 F1 { get; }

    /// <summary>Distance to the second-nearest feature point; <c>F2 - F1</c> is small near cell edges.</summary>
    public Fixed32 F2 { get; }

    /// <summary>The lattice hash of the nearest feature's cell: a stable per-cell id and 32 bits of per-cell randomness.</summary>
    public uint Cell { get; }

    public bool Equals(CellularSample other) => F1 == other.F1 && F2 == other.F2 && Cell == other.Cell;

    public override bool Equals(object? obj) => obj is CellularSample other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(F1.Raw, F2.Raw, Cell);

    public static bool operator ==(CellularSample left, CellularSample right) => left.Equals(right);

    public static bool operator !=(CellularSample left, CellularSample right) => !left.Equals(right);
}
