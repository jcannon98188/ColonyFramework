using Colony.Fields;

namespace Colony.Jobs;

/// <summary>
/// A work item (spec section 9.2): an ordinary saved Component on an Entity that also carries a
/// <see cref="TilePos"/>. <see cref="Type"/> is the Game's work type (0..<see cref="WorkIndexOptions.WorkTypeCount"/>-1);
/// an item is listed by the <see cref="WorkIndex"/> only while <see cref="IsAvailable"/>. Games flip availability
/// when a Claim is granted or released, or when the job finishes; the index follows the Component change feed.
/// </summary>
public readonly record struct WorkItem(int Type, byte Flags)
{
    /// <summary>Bit of <see cref="Flags"/>: the item is open for takers and appears in the index.</summary>
    public const byte AvailableFlag = 1;

    public bool IsAvailable => (Flags & AvailableFlag) != 0;

    /// <summary>An available item of <paramref name="type"/>.</summary>
    public static WorkItem Available(int type) => new(type, AvailableFlag);

    /// <summary>An item of <paramref name="type"/> that is not open (claimed, in progress, paused).</summary>
    public static WorkItem Taken(int type) => new(type, 0);

    /// <summary>The same item with its availability set.</summary>
    public WorkItem WithAvailable(bool available)
        => new(Type, available ? (byte)(Flags | AvailableFlag) : (byte)(Flags & ~AvailableFlag));
}

/// <summary>
/// One result of a <see cref="WorkIndex"/> query: the item Entity, the tile it was indexed at, its work type and
/// its squared Euclidean tile distance from the query origin. Results are ordered by distance, then Entity id.
/// </summary>
public readonly record struct WorkHit(EntityId Entity, TilePos Position, int Type, long DistanceSquared);

/// <summary>
/// A filter over indexed work items, as a struct so queries from parallel Systems allocate nothing. The
/// reachability filter of spec section 9.2 ships as <see cref="ReachableWorkFilter"/>; write your own struct to
/// combine it with Game rules (zones, skills).
/// </summary>
public interface IWorkFilter
{
    public bool Accepts(EntityId entity, in TilePos position, int type);
}

/// <summary>The filter that accepts every available item.</summary>
public readonly struct AnyWorkItem : IWorkFilter, IEquatable<AnyWorkItem>
{
    public bool Accepts(EntityId entity, in TilePos position, int type) => true;

    public bool Equals(AnyWorkItem other) => true;

    public override bool Equals(object? obj) => obj is AnyWorkItem;

    public override int GetHashCode() => 0;

    public static bool operator ==(AnyWorkItem left, AnyWorkItem right) => true;

    public static bool operator !=(AnyWorkItem left, AnyWorkItem right) => false;
}
