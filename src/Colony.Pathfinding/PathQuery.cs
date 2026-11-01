using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>How a path query or request ended (spec section 9.1).</summary>
public enum PathStatus : byte
{
    /// <summary>A path exists; its waypoints and cost are available.</summary>
    Found,

    /// <summary>No path: an endpoint is impassable or outside the World, or the endpoints lie in different regions.</summary>
    Unreachable,

    /// <summary>The search had to cross an Unloaded chunk; <c>MissingChunk</c> names it.</summary>
    NeedsLoading,

    /// <summary>A deferred request was not processed by its delivery Tick under the work budget.</summary>
    DeadlineMissed,
}

/// <summary>
/// The outcome of a synchronous <see cref="PathfindingService.FindPath"/>: the status, the path cost in tiles
/// (integer, one per face move), how many waypoints the whole path has (the caller's span may hold fewer) and
/// the work units spent (abstract nodes plus local cells expanded).
/// </summary>
public readonly record struct PathQuery(PathStatus Status, int Cost, int WaypointCount, ChunkCoord MissingChunk, long WorkUnits)
{
    public bool IsFound => Status == PathStatus.Found;
}
