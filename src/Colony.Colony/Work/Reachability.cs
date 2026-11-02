using Colony.Fields;
using Colony.Pathfinding;

namespace Colony.Jobs;

/// <summary>
/// The reachability filter of spec section 9.2 for the <see cref="WorkIndex"/>: accepts an item only when a mover
/// of <see cref="MovementClass"/> standing at <see cref="From"/> can reach its tile
/// (<see cref="PathfindingService.Reachable"/>: constant time, allocation free). Region ids never move inside a
/// Phase, so the filter is deterministic from parallel chunk jobs; doors opening or closing take effect at the
/// next pathfinding sync point (the Late Phase, or the start of the next Tick for End-Phase and host writes).
/// </summary>
/// <param name="Pathfinding">The World's service, <c>world.Pathfinding()</c>.</param>
/// <param name="From">Where the requesting agent stands.</param>
/// <param name="MovementClass">The requesting agent's movement class.</param>
public readonly record struct ReachableWorkFilter(PathfindingService Pathfinding, TilePos From, MovementClass MovementClass) : IWorkFilter
{
    public bool Accepts(EntityId entity, in TilePos position, int type) => Pathfinding.Reachable(From, position, MovementClass);
}

/// <summary>
/// The reachability filter of spec section 9.2 for the <see cref="HaulingMatcher"/>: allows a (supply, demand) pair
/// only when a hauler of <see cref="MovementClass"/> can carry from the supply's tile to the demand's tile, and,
/// when <see cref="Hauler"/> is set, also walk from there to the supply. Same determinism and timing as
/// <see cref="ReachableWorkFilter"/>.
/// </summary>
/// <param name="Pathfinding">The World's service, <c>world.Pathfinding()</c>.</param>
/// <param name="MovementClass">The hauler's movement class.</param>
/// <param name="Hauler">Where the hauler stands, or null to check only supply to demand.</param>
public readonly record struct ReachableHaulFilter(PathfindingService Pathfinding, MovementClass MovementClass, TilePos? Hauler = null) : IHaulFilter
{
    public bool CanHaul(in HaulSupply supply, in HaulDemand demand)
        => Pathfinding.Reachable(supply.Position, demand.Position, MovementClass)
            && (Hauler is not { } hauler || Pathfinding.Reachable(hauler, supply.Position, MovementClass));
}
