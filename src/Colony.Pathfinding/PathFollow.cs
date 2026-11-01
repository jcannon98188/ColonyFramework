using Colony.Fields;
using Colony.Numerics;

namespace Colony.Pathfinding;

/// <summary>What a <see cref="PathfindingService.Follow"/> call reports.</summary>
public enum PathFollowStatus : byte
{
    /// <summary>Still on the way; the state moved by the distance given.</summary>
    Moving,

    /// <summary>At the centre of the last waypoint.</summary>
    Arrived,

    /// <summary>The next waypoint cannot be reached inside the unit's chunk (the terrain changed under the path); re-request.</summary>
    Blocked,
}

/// <summary>
/// The state of a grid Entity following a path (spec section 9.1 path-follow helper): its tile, its fixed-point
/// offset inside the tile (each axis in <c>[0, 1)</c>, the centre at one half), the face-adjacent tile it is moving
/// to and the index of the waypoint it is heading for. Plain unmanaged data: store it in a Component beside the
/// waypoints and advance it with <see cref="PathfindingService.Follow"/>. Games own movement; this helper only
/// walks the tiles.
/// </summary>
/// <param name="Tile">The tile the Entity stands in.</param>
/// <param name="Offset">Offset inside <paramref name="Tile"/>, each axis in <c>[0, 1)</c>.</param>
/// <param name="Next">The tile being moved to (equal to <paramref name="Tile"/> while none is chosen).</param>
/// <param name="Waypoint">Index of the waypoint being approached.</param>
public record struct PathFollowState(TilePos Tile, SubTileOffset Offset, TilePos Next, int Waypoint)
{
    /// <summary>The tile centre.</summary>
    public static SubTileOffset Centre => new(new Fixed32x3(Fixed32.Half, Fixed32.Half, Fixed32.Half));

    /// <summary>A follower standing at the centre of <paramref name="tile"/>, about to head for the first waypoint.</summary>
    public static PathFollowState At(TilePos tile) => new(tile, Centre, tile, 0);

    /// <summary>The position in tile units: tile plus offset.</summary>
    public Fixed32x3 Position => new(
        Fixed32.FromInt32(Tile.X) + Offset.X,
        Fixed32.FromInt32(Tile.Y) + Offset.Y,
        Fixed32.FromInt32(Tile.Z) + Offset.Z);
}

/// <summary>The movement rule behind <see cref="PathfindingService.Follow"/>; separate so it can be unit tested without a World.</summary>
internal static class PathFollower
{
    public static PathFollowStatus Advance(RegionMap map, ref PathFollowState state, ReadOnlySpan<TilePos> waypoints, Fixed32 distance)
    {
        Span<int> dist = stackalloc int[FieldChunk.CellCount];
        Span<ushort> queue = stackalloc ushort[FieldChunk.CellCount];
        var centre = PathFollowState.Centre;
        while (true)
        {
            if (state.Next == state.Tile && state.Offset == centre)
            {
                // At a tile centre with no move in progress: pick the next face move toward the current waypoint.
                while (state.Waypoint < waypoints.Length && waypoints[state.Waypoint] == state.Tile)
                {
                    state.Waypoint++;
                }

                if (state.Waypoint >= waypoints.Length)
                {
                    return PathFollowStatus.Arrived;
                }

                if (!LocalStep.Toward(map, state.Tile, waypoints[state.Waypoint], dist, queue, out var next))
                {
                    return PathFollowStatus.Blocked;
                }

                state.Next = next;
            }

            if (distance <= Fixed32.Zero)
            {
                return PathFollowStatus.Moving;
            }

            // Move toward the centre of Next, one axis at a time (face moves are axis aligned), then renormalise the tile.
            var delta = new Fixed32x3(
                Fixed32.FromInt32(state.Next.X - state.Tile.X) + centre.X - state.Offset.X,
                Fixed32.FromInt32(state.Next.Y - state.Tile.Y) + centre.Y - state.Offset.Y,
                Fixed32.FromInt32(state.Next.Z - state.Tile.Z) + centre.Z - state.Offset.Z);
            var x = Step(state.Offset.X, delta.X, ref distance);
            var y = Step(state.Offset.Y, delta.Y, ref distance);
            var z = Step(state.Offset.Z, delta.Z, ref distance);
            int tx = state.Tile.X;
            int ty = state.Tile.Y;
            int tz = state.Tile.Z;
            Normalise(ref x, ref tx);
            Normalise(ref y, ref ty);
            Normalise(ref z, ref tz);
            state.Tile = new TilePos(tx, ty, tz);
            state.Offset = new SubTileOffset(new Fixed32x3(x, y, z));
            if (state.Tile == state.Next && state.Offset == centre)
            {
                continue; // reached the centre of Next; choose the next move with what distance remains
            }

            if (distance <= Fixed32.Zero)
            {
                return PathFollowStatus.Moving;
            }
        }
    }

    private static Fixed32 Step(Fixed32 offset, Fixed32 delta, ref Fixed32 distance)
    {
        if (delta == Fixed32.Zero || distance <= Fixed32.Zero)
        {
            return offset;
        }

        var magnitude = Fixed32.Abs(delta);
        var step = Fixed32.Min(magnitude, distance);
        distance -= step;
        return delta > Fixed32.Zero ? offset + step : offset - step;
    }

    private static void Normalise(ref Fixed32 offset, ref int tile)
    {
        while (offset >= Fixed32.One)
        {
            offset -= Fixed32.One;
            tile++;
        }

        while (offset < Fixed32.Zero)
        {
            offset += Fixed32.One;
            tile--;
        }
    }
}
