namespace Colony.Fields;

/// <summary>
/// One result of a <see cref="SpatialIndex"/> query: the Entity, the tile it was indexed at and its squared
/// Euclidean tile distance from the query origin. Results are ordered by distance, then Entity id.
/// </summary>
public readonly record struct SpatialHit(EntityId Entity, TilePos Position, long DistanceSquared);

/// <summary>An axis-aligned, inclusive box of tiles.</summary>
public readonly record struct TileBox(TilePos Min, TilePos Max)
{
    /// <summary>The box of every tile within <paramref name="radius"/> tiles of <paramref name="center"/> along each axis.</summary>
    public static TileBox Around(TilePos center, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        return new(new TilePos(center.X - radius, center.Y - radius, center.Z - radius), new TilePos(center.X + radius, center.Y + radius, center.Z + radius));
    }

    public bool Contains(TilePos tile)
        => tile.X >= Min.X && tile.X <= Max.X && tile.Y >= Min.Y && tile.Y <= Max.Y && tile.Z >= Min.Z && tile.Z <= Max.Z;

    public override string ToString() => $"[{Min}..{Max}]";
}

/// <summary>
/// A filter over indexed Entities, as a struct so queries from parallel Systems allocate nothing. Implement
/// it on a readonly struct that carries whatever the filter needs (a Component column, a mask, a World).
/// </summary>
public interface ISpatialPredicate
{
    public bool Matches(EntityId entity, in TilePos position);
}

/// <summary>The predicate that accepts every Entity.</summary>
public readonly struct AnyEntity : ISpatialPredicate, IEquatable<AnyEntity>
{
    public bool Matches(EntityId entity, in TilePos position) => true;

    public bool Equals(AnyEntity other) => true;

    public override bool Equals(object? obj) => obj is AnyEntity;

    public override int GetHashCode() => 0;

    public static bool operator ==(AnyEntity left, AnyEntity right) => true;

    public static bool operator !=(AnyEntity left, AnyEntity right) => false;
}
