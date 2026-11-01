using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>
/// A deferred path request (spec section 9.1): emit it on <see cref="PathfindingBuilder.RequestQueue"/> from any
/// System (or from a host command buffer between Ticks). Requests emitted in Tick T are taken in at the End
/// Phase of T and answered with a <see cref="PathResult"/> at the Begin Phase of Tick T + <see cref="PathfindingOptions.ResultDelayTicks"/>,
/// in canonical order: <paramref name="Priority"/> (0 is most urgent), then request id (submission order).
/// </summary>
/// <param name="Requester">The Entity asking, echoed in the result; the service does not read it.</param>
/// <param name="From">Start tile.</param>
/// <param name="To">Goal tile.</param>
/// <param name="MovementClass">The mover's class.</param>
/// <param name="Priority">Priority class, 0 most urgent.</param>
/// <param name="Tag">Caller data echoed in the result.</param>
public readonly record struct PathRequest(EntityId Requester, TilePos From, TilePos To, MovementClass MovementClass, byte Priority, uint Tag);

/// <summary>
/// The answer to a <see cref="PathRequest"/>, emitted on <see cref="PathfindingBuilder.ResultQueue"/> at the
/// Begin Phase of the delivery Tick and readable through that Tick. The waypoints (start, chunk transitions,
/// goal) are read with <see cref="PathfindingService.Waypoints"/>; they stay valid until the next delivery Tick.
/// </summary>
/// <param name="RequestId">Sequential id given at intake; canonical order is (priority, id).</param>
/// <param name="Requester">Echoed from the request.</param>
/// <param name="Tag">Echoed from the request.</param>
/// <param name="Status">Found, Unreachable, NeedsLoading (see <paramref name="MissingChunk"/>) or DeadlineMissed (not computed under the budget by the delivery Tick).</param>
/// <param name="Cost">Path cost in tiles when found, else 0.</param>
/// <param name="WaypointOffset">Position of the waypoints in the delivery arena.</param>
/// <param name="WaypointCount">Number of waypoints, 0 unless found.</param>
/// <param name="MissingChunk">The Unloaded chunk the path needed, when the status is NeedsLoading.</param>
/// <param name="WorkUnits">Work units the search spent (0 for a missed deadline).</param>
public readonly record struct PathResult(long RequestId, EntityId Requester, uint Tag, PathStatus Status, int Cost, int WaypointOffset, int WaypointCount, ChunkCoord MissingChunk, long WorkUnits)
{
    public bool IsFound => Status == PathStatus.Found;
}

/// <summary>Totals of the request queue since the World was created (diagnostics; not simulation state).</summary>
/// <param name="Submitted">Requests taken in.</param>
/// <param name="Computed">Searches run.</param>
/// <param name="Delivered">Results emitted.</param>
/// <param name="Deferred">Request-Ticks left uncomputed because the budget was spent (one per request per Tick).</param>
/// <param name="DeadlinesMissed">Results delivered as <see cref="PathStatus.DeadlineMissed"/>.</param>
/// <param name="WorkUnits">Work units spent by the queue's searches.</param>
public readonly record struct PathQueueCounters(long Submitted, long Computed, long Delivered, long Deferred, long DeadlinesMissed, long WorkUnits);
